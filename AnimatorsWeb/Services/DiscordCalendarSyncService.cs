using System.Text.Json;
using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class DiscordCalendarSyncService
    {
        private readonly AppDbContext _db;
        private readonly DiscordClient _client;
        private readonly ILogger<DiscordCalendarSyncService> _logger;
        private readonly DiscordRecurringEventClient _recurringClient;


        public DiscordCalendarSyncService(
            AppDbContext db,
            DiscordClient client,
            DiscordRecurringEventClient recurringClient,
            ILogger<DiscordCalendarSyncService> logger)
        {
            _db = db;
            _client = client;
            _recurringClient = recurringClient;
            _logger = logger;
        }

        public DiscordGuild? GetGuildForApply() => _client.Guilds.Values.FirstOrDefault();

        public async Task<ulong?> PushAsync(CalendarEvent ev, CancellationToken ct = default, string? imageDataUri = null)
        {
            if (ev.SkipDiscordSync) return null;

            var guild = GetGuildForApply();
            if (guild == null) return null;
            if (string.IsNullOrWhiteSpace(ev.Location)) return null;
            if (ev.DiscordEventId != null && !string.IsNullOrEmpty(ev.DiscordSnapshot)
                && ev.DiscordSnapshot == BuildSnapshot(ev))
                return ev.DiscordEventId;

            bool isChannel = ulong.TryParse(ev.Location, out var channelId);
            ulong? channel = isChannel ? channelId : null;
            string? externalLocation = isChannel ? null : ev.Location;

            try
            {
                if (ev.DiscordEventId == null)
                {
                    var newId = await _recurringClient.CreateSingleAsync(
                        guild.Id, channel, ev.Title, ev.Description,
                        ev.StartUtc, ev.EndUtc, externalLocation, ct, imageDataUri);
                    if (string.IsNullOrEmpty(newId) || !ulong.TryParse(newId, out var parsed))
                        return null;
                    ev.DiscordEventId = parsed;
                    ev.Source = "Site";
                }
                else
                {
                    var ok = await _recurringClient.ModifySingleAsync(
                        guild.Id, ev.DiscordEventId.Value, ev.Title, ev.Description,
                        ev.StartUtc, ev.EndUtc, channel, externalLocation, ct, imageDataUri);
                    if (!ok) return null;
                }

                ev.LastSyncedUtc = DateTime.UtcNow;
                ev.DiscordSnapshot = BuildSnapshot(ev);
                return ev.DiscordEventId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord push failed for calendar event {Id}", ev.Id);
                return null;
            }
        }

        public async Task DeleteInDiscordAsync(ulong discordEventId)
        {
            var guild = GetGuildForApply();
            if (guild == null) return;

            try
            {
                var allEvents = await guild.GetEventsAsync();
                var existing = allEvents.FirstOrDefault(e => e.Id == discordEventId);
                if (existing != null)
                    await guild.DeleteEventAsync(existing);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord delete failed for event {Id}", discordEventId);
            }
        }

        public async Task PullAsync(bool force = false, CancellationToken ct = default)
        {
            var started = DateTime.UtcNow;
            var guild = GetGuildForApply();
            if (guild == null) return;

            Dictionary<ulong, DiscordEventRawInfo> rawMap;
            try
            {
                rawMap = await _recurringClient.GetAllEventsRawAsync(guild.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord pull failed: GetAllEventsRawAsync");
                return;
            }

            if (rawMap.Count == 0) return;

            // Серии, которыми мы управляем через пресеты — не импортируем как CalendarEvent,
            // но подтягиваем их правило/исключения/атрибуты из Discord (равноправная синхронизация).
            var ownedSeriesPresets = await _db.EventPresets
                .Where(p => p.IsDiscordSeries && p.DiscordSeriesEventId != null)
                .ToListAsync(ct);
            var ownedSeriesSet = ownedSeriesPresets
                .Select(p => p.DiscordSeriesEventId!.Value)
                .ToHashSet();
            var ownedByDiscordId = ownedSeriesPresets
                .ToDictionary(p => p.DiscordSeriesEventId!.Value);

            foreach (var kv in rawMap)
            {
                var de = kv.Value;
                if (ownedSeriesSet.Contains(de.Id))
                {
                    if (ownedByDiscordId.TryGetValue(de.Id, out var ownedPreset) && !ownedPreset.IsDeleted)
                    {
                        // Список событий Discord не гарантирует exceptions. Полный GET нужен
                        // только когда есть что сравнивать; остальное покрывает gateway.
                        bool needFull = !string.IsNullOrEmpty(ownedPreset.RecurrenceExceptionsJson)
                                        || de.Exceptions.Count > 0
                                        || string.IsNullOrEmpty(de.RecurrenceRuleJson)
                                        || de.Status is 3 or 4;
                        var full = needFull
                            ? await _recurringClient.GetEventAsync(guild.Id, de.Id, ct) ?? de
                            : de;
                        CalendarTrace.Write("PULL", $"preset={ownedPreset.Id} discordId={de.Id} status={full.Status} " +
                            $"rule={Truncate(full.RecurrenceRuleJson)} ex={full.Exceptions.Count} " +
                            $"localEx={Truncate(ownedPreset.RecurrenceExceptionsJson)}");
                        if (full.Status == 3 || full.Status == 4)
                        {
                            ownedPreset.IsDeleted = true;
                            await _db.SaveChangesAsync(ct);
                        }
                        else
                        {
                            await SyncOwnedSeriesFromDiscordAsync(ownedPreset, full, force, ct);
                        }
                    }
                    continue;
                }

                // Отменённые/завершённые события не импортируем (и удалим локальные ниже).
                if (de.Status is 3 or 4) continue;

                var source = de.IsRecurring
                    ? await _recurringClient.GetEventAsync(guild.Id, de.Id, ct) ?? de
                    : de;
                await UpsertFromDiscordAsync(source, ct);
            }

            var now = DateTime.UtcNow;
            // Отменённые/завершённые в Discord события считаем отсутствующими:
            // на сетке их быть не должно.
            var discordIds = rawMap
                .Where(kv => kv.Value.Status is not (3 or 4))
                .Select(kv => kv.Key)
                .ToHashSet();
            var missing = await _db.CalendarEvents
                .Where(e => e.DiscordEventId != null
                         && e.StartUtc > now
                         && !discordIds.Contains(e.DiscordEventId.Value))
                .ToListAsync(ct);

            _db.CalendarEvents.RemoveRange(missing);

            await _db.SaveChangesAsync(ct);
            CalendarChangeNotifier.Bump();
            CalendarTrace.Write("PULL", $"done events={rawMap.Count} removed={missing.Count} " +
                $"elapsed={(DateTime.UtcNow - started).TotalMilliseconds:F0}ms");
        }

        /// <summary>
        /// Точечный Pull по одному Discord-событию. Используется gateway-хендлером,
        /// чтобы сайт узнавал об изменениях сразу, без захода модератора на страницу.
        /// </summary>
        public async Task SyncOneAsync(ulong discordEventId, CancellationToken ct = default)
        {
            var guild = GetGuildForApply();
            if (guild == null) return;

            var raw = await _recurringClient.GetEventAsync(guild.Id, discordEventId, ct);
            if (raw == null) return;

            if (raw.Status == 3 || raw.Status == 4)
            {
                await DeleteByDiscordIdAsync(discordEventId, ct);
                return;
            }

            var ownedPreset = await _db.EventPresets
                .FirstOrDefaultAsync(p => p.IsDiscordSeries && p.DiscordSeriesEventId == discordEventId, ct);
            if (ownedPreset != null)
            {
                if (!ownedPreset.IsDeleted)
                {
                    await SyncOwnedSeriesFromDiscordAsync(ownedPreset, raw, force: false, ct);
                    CalendarChangeNotifier.Bump();
                }
                return;
            }

            await UpsertFromDiscordAsync(raw, ct);
            await _db.SaveChangesAsync(ct);
            CalendarChangeNotifier.Bump();
        }

        public async Task DeleteByDiscordIdAsync(ulong discordEventId, CancellationToken ct = default)
        {
            var ev = await _db.CalendarEvents
                .FirstOrDefaultAsync(e => e.DiscordEventId == discordEventId, ct);
            if (ev != null)
            {
                // Применение превращает одиночное событие в серию: старое одиночное
                // событие удаляется намеренно, карточка должна остаться на сетке.
                EventPreset? cardPreset = null;
                if (ev.EventPresetId.HasValue)
                    cardPreset = await _db.EventPresets
                        .FindAsync(new object?[] { ev.EventPresetId.Value }, ct);

                if (cardPreset?.IsDiscordSeries == true && cardPreset.PendingApply)
                {
                    ev.DiscordEventId = null;
                    ev.DiscordSnapshot = null;
                    await _db.SaveChangesAsync(ct);
                    return;
                }

                _db.CalendarEvents.Remove(ev);
                await _db.SaveChangesAsync(ct);
            }

            // Серию, удалённую прямо в Discord, помечаем как удалённую — Apply добьёт её в БД.
            var preset = await _db.EventPresets
                .FirstOrDefaultAsync(p => p.IsDiscordSeries && p.DiscordSeriesEventId == discordEventId, ct);
            if (preset != null && !preset.IsDeleted)
            {
                preset.IsDeleted = true;
                await _db.SaveChangesAsync(ct);
            }

            CalendarChangeNotifier.Bump();
        }

        private async Task UpsertFromDiscordAsync(DiscordEventRawInfo de, CancellationToken ct)
        {
            var existing = await _db.CalendarEvents
                .FirstOrDefaultAsync(e => e.DiscordEventId == de.Id, ct);

            CalendarEvent ev;
            bool changed;

            if (existing != null)
            {
                var newSnapshot = BuildSnapshotFromRaw(de);
                changed = existing.DiscordSnapshot != newSnapshot;
                if (changed)
                {
                    existing.Title = string.IsNullOrEmpty(de.Name) ? existing.Title : de.Name;
                    existing.Description = string.IsNullOrEmpty(de.Description) ? null : de.Description;
                    existing.StartUtc = de.StartUtc;
                    if (de.EndUtc.HasValue) existing.EndUtc = de.EndUtc.Value;
                    existing.Location = de.ChannelId?.ToString() ?? existing.Location;
                    existing.Source = "Discord";
                    existing.DiscordSnapshot = newSnapshot;
                    existing.LastSyncedUtc = DateTime.UtcNow;
                }
                ev = existing;
            }
            else
            {
                ev = new CalendarEvent
                {
                    Title = string.IsNullOrEmpty(de.Name) ? "(без названия)" : de.Name,
                    Description = string.IsNullOrEmpty(de.Description) ? null : de.Description,
                    Location = de.ChannelId?.ToString(),
                    StartUtc = de.StartUtc,
                    EndUtc = de.EndUtc ?? de.StartUtc.AddHours(2),
                    DiscordEventId = de.Id,
                    Source = "Discord",
                    DiscordSnapshot = BuildSnapshotFromRaw(de),
                    LastSyncedUtc = DateTime.UtcNow
                };
                _db.CalendarEvents.Add(ev);
                changed = true;
            }

            if (de.IsRecurring)
            {
                ev.IsRecurring = true;
                ev.RecurrenceRuleJson = de.RecurrenceRuleJson;
                var discordExceptions = de.Exceptions.Select(x => new PresetExceptionRecord
                {
                    OriginalStartUtc = x.OriginalStartUtc,
                    IsCanceled = x.IsCanceled,
                    NewStartUtc = x.NewStartUtc
                }).ToList();
                var localExceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                var merged = new List<PresetExceptionRecord>(localExceptions);
                foreach (var d in discordExceptions)
                {
                    var idx = merged.FindIndex(x => x.OriginalStartUtc == d.OriginalStartUtc);
                    if (idx >= 0) merged[idx] = d;
                    else merged.Add(d);
                }
                ev.RecurrenceExceptionsJson = EventExceptionJson.Serialize(merged);
            }
            else if (ev.IsRecurring && ev.Source == "Discord")
            {
                ev.IsRecurring = false;
                ev.RecurrenceRuleJson = null;
                ev.RecurrenceExceptionsJson = null;
            }

            if (changed || ev.EventPresetId == null)
                await SyncPresetFromDiscordAsync(ev, de, ct);
        }

        /// <summary>
        /// Привязывает к событию пресет: если уже привязан — ничего не делает;
        /// иначе ищет существующий по (title + duration + location) и, если не нашёл, создаёт новый.
        /// Так все события из Discord получают «шаблон», который можно редактировать и использовать в паттернах.
        /// </summary>
        private async Task SyncPresetFromDiscordAsync(
    CalendarEvent ev, DiscordEventRawInfo de, CancellationToken ct)
        {
            var title = string.IsNullOrEmpty(de.Name) ? "(без названия)" : de.Name;
            var endUtc = de.EndUtc ?? de.StartUtc.AddHours(2);
            var durationHours = (int)Math.Max(1, Math.Ceiling(endUtc.Subtract(de.StartUtc).TotalHours));
            var location = de.ChannelId?.ToString();
            var description = string.IsNullOrEmpty(de.Description) ? null : de.Description;

            EventPreset? preset = null;
            if (ev.EventPresetId.HasValue)
                preset = await _db.EventPresets.FindAsync(new object?[] { ev.EventPresetId.Value }, ct);

            if (preset == null)
            {
                preset = await _db.EventPresets.FirstOrDefaultAsync(p =>
                    p.DefaultTitle == title
                    && p.DurationHours == durationHours
                    && p.Location == location, ct);
                if (preset == null)
                {
                    preset = _db.EventPresets.Local.FirstOrDefault(p =>
                        p.DefaultTitle == title
                        && p.DurationHours == durationHours
                        && p.Location == location);
                }
            }

            if (preset == null)
            {
                preset = new EventPreset
                {
                    Name = title,
                    DefaultTitle = title,
                    DefaultDescription = description,
                    DurationHours = durationHours,
                    Location = location,
                    CreateInDiscord = true
                };
                _db.EventPresets.Add(preset);
            }
            else
            {
                preset.DefaultTitle = title;
                preset.DefaultDescription = description;
                preset.DurationHours = durationHours;
                preset.Location = location;
            }

            if (de.IsRecurring && !string.IsNullOrEmpty(de.RecurrenceRuleJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(de.RecurrenceRuleJson);
                    var root = doc.RootElement;
                    preset.IsRecurring = true;
                    preset.RecurrenceFrequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2;
                    preset.RecurrenceInterval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1;
                    if (root.TryGetProperty("by_weekday", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var days = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        preset.RecurrenceByWeekdays = string.Join(",", days);
                    }
                    else
                    {
                        preset.RecurrenceByWeekdays = null;
                    }
                }
                catch { }
            }
            else
            {
                preset.IsRecurring = false;
                preset.RecurrenceFrequency = null;
                preset.RecurrenceByWeekdays = null;
            }

            await _db.SaveChangesAsync(ct);
            ev.EventPresetId = preset.Id;
        }

        /// <summary>
        /// Подтягиваем изменения, сделанные модератором прямо в Discord, для серии,
        /// которой владеет пресет. Фантомных CalendarEvent для самой серии не создаём.
        ///
        /// Discord здесь — фактическое состояние серии: правило, исключения и атрибуты
        /// заменяются целиком (а не сливаются с локальными). Иначе старые локальные
        /// отмены навсегда «затеняли» реальные события и Pull выглядел как «ничего не изменилось».
        /// </summary>
        private async Task SyncOwnedSeriesFromDiscordAsync(
            EventPreset preset, DiscordEventRawInfo de, bool force, CancellationToken ct)
        {
            // Есть локальные правки, ещё не отправленные в Discord — автоматический Pull
            // не должен их затирать. Ручная кнопка «Обновить из Discord» вызывает force=true.
            if (preset.PendingApply && !force)
            {
                CalendarTrace.Write("PULL", $"preset={preset.Id} skipped: pending apply");
                return;
            }

            bool changed = false;

            if (!string.IsNullOrWhiteSpace(de.Name) && preset.DefaultTitle != de.Name)
            { preset.DefaultTitle = de.Name; changed = true; }
            if (preset.DefaultDescription != de.Description)
            { preset.DefaultDescription = de.Description; changed = true; }
            if (de.EndUtc.HasValue)
            {
                var hours = Math.Max(1, (int)Math.Ceiling((de.EndUtc.Value - de.StartUtc).TotalHours));
                if (preset.DurationHours != hours)
                { preset.DurationHours = hours; changed = true; }
            }
            if (de.ChannelId.HasValue)
            {
                var location = de.ChannelId.Value.ToString();
                if (preset.Location != location)
                { preset.Location = location; changed = true; }
            }

            if (de.IsRecurring && !string.IsNullOrWhiteSpace(de.RecurrenceRuleJson))
            {
                preset.IsRecurring = true;

                if (!DiscordRuleJson.Same(preset.RecurrenceRuleJson, de.RecurrenceRuleJson))
                {
                    preset.RecurrenceRuleJson = de.RecurrenceRuleJson;
                    changed = true;
                }

                // Настройки повторения всегда отражают фактическое правило Discord,
                // чтобы следующий Apply не откатил его обратно.
                try
                {
                    using var doc = JsonDocument.Parse(de.RecurrenceRuleJson);
                    var root = doc.RootElement;

                    var frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2;
                    if (preset.RecurrenceFrequency != frequency)
                    { preset.RecurrenceFrequency = frequency; changed = true; }

                    var interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0
                        ? i.GetInt32() : 1;
                    if (preset.RecurrenceInterval != interval)
                    { preset.RecurrenceInterval = interval; changed = true; }

                    var weekdays = root.TryGetProperty("by_weekday", out var w) && w.ValueKind == JsonValueKind.Array
                        ? string.Join(",", w.EnumerateArray().Select(x => x.GetInt32()))
                        : null;
                    if (preset.RecurrenceByWeekdays != weekdays)
                    { preset.RecurrenceByWeekdays = weekdays; changed = true; }

                    if (root.TryGetProperty("start", out var startEl)
                        && startEl.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(startEl.GetString(), null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal |
                            System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var ruleStart))
                    {
                        var timeOfDay = DateTime.SpecifyKind(ruleStart, DateTimeKind.Utc).TimeOfDay;
                        if (preset.DefaultStartTimeUtc != timeOfDay)
                        {
                            preset.DefaultStartTimeUtc = timeOfDay;
                            changed = true;
                        }
                    }
                }
                catch { /* rule всё равно сохранён как есть */ }

                CalendarTrace.Write("PULL",
                    $"preset={preset.Id} rule: freq={preset.RecurrenceFrequency} " +
                    $"interval={preset.RecurrenceInterval} weekdays={preset.RecurrenceByWeekdays ?? "-"} " +
                    $"startTime={preset.DefaultStartTimeUtc}");
            }

            // Discord присылает ПОЛНЫЙ список исключений серии. Именно он и является
            // фактическим состоянием: заменяем локальный список целиком.
            var discordExceptions = de.Exceptions
                .Select(e => new PresetExceptionRecord
                {
                    OriginalStartUtc = DateTime.SpecifyKind(e.OriginalStartUtc, DateTimeKind.Utc),
                    IsCanceled = e.IsCanceled,
                    NewStartUtc = e.NewStartUtc.HasValue
                        ? DateTime.SpecifyKind(e.NewStartUtc.Value, DateTimeKind.Utc)
                        : null
                })
                .OrderBy(e => e.OriginalStartUtc)
                .ToList();

            if (!SameExceptions(preset.RecurrenceExceptionsJson, discordExceptions))
            {
                preset.RecurrenceExceptionsJson =
                    discordExceptions.Count == 0 ? null : JsonSerializer.Serialize(discordExceptions);
                changed = true;
                CalendarTrace.Write("PULL",
                    $"preset={preset.Id} exceptions replaced: discord={discordExceptions.Count}");
            }

            if (changed) await _db.SaveChangesAsync(ct);

            // Карточки приводим к состоянию Discord ВСЕГДА, даже если правило и исключения
            // формально не изменились: сетка могла разойтись из-за локальных правок.
            await ReconcileOwnedSeriesAsync(preset, ct);

            preset.PendingApply = false;
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Приводит карточки пресета к состоянию «правило + исключения».
        /// Используется и Pull'ом (зеркалим Discord), и Apply (после отправки).
        ///
        /// Главное правило: если вхождения нет в Discord (его нет среди желаемых),
        /// карточка удаляется с сетки. Иначе локальные переносы/правки «залипали»
        /// и Pull не сбрасывал изменения.
        /// </summary>
        public async Task ReconcileOwnedSeriesAsync(EventPreset preset, CancellationToken ct)
        {
            var rule = ParseSeriesRule(preset.RecurrenceRuleJson);
            if (rule == null) return;

            var rows = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id)
                .ToListAsync(ct);

            int removed = 0;

            // Старый импорт самой серии как CalendarEvent — больше не нужен.
            if (preset.DiscordSeriesEventId.HasValue)
            {
                foreach (var legacy in rows
                             .Where(e => e.DiscordEventId == preset.DiscordSeriesEventId)
                             .ToList())
                {
                    _db.CalendarEvents.Remove(legacy);
                    rows.Remove(legacy);
                    removed++;
                }
            }

            var now = DateTime.UtcNow;
            var from = now.AddDays(-1);
            var horizon = now.AddDays(SeriesOccurrenceExtender.HorizonWeeks * 7);

            // Окно должно покрывать все уже материализованные карточки — в том числе
            // «Продление» дальше 13 недель: иначе Pull снёс бы их как «лишние».
            if (rows.Count > 0)
            {
                var minRow = rows.Min(e => Utc(e.StartUtc));
                var maxRow = rows.Max(e => Utc(e.StartUtc));
                if (minRow < from) from = minRow;
                if (maxRow >= horizon) horizon = maxRow.AddDays(1);
            }

            var exceptions = ParseSeriesExceptions(preset.RecurrenceExceptionsJson);
            var desired = SeriesReconciler.Desired(rule, exceptions, from, horizon);

            // Нераскладываемое правило (monthly/yearly) — не трогаем карточки вообще.
            if (desired.Count == 0)
            {
                if (removed > 0) await _db.SaveChangesAsync(ct);
                CalendarTrace.Write("PULL",
                    $"preset={preset.Id} reconcile skipped: desired=0 removed={removed}");
                return;
            }

            var plan = SeriesReconciler.Build(
                rows.Select(e => Utc(e.StartUtc)).ToList(), desired);

            int added = plan.Add.Count, revived = 0, updated = 0;
            var dropped = new List<string>();

            foreach (var (index, target) in plan.Keep)
            {
                var ev = rows[index];
                if (ev.IsHidden) revived++;
                ApplyPresetCardFields(ev, preset, target);
                updated++;
            }

            foreach (var index in plan.Remove)
            {
                var ev = rows[index];
                _db.CalendarEvents.Remove(ev);
                removed++;
                var start = Utc(ev.StartUtc);
                if (dropped.Count < 12) dropped.Add($"{start:yyyy-MM-dd HH:mm}");
            }

            foreach (var target in plan.Add)
            {
                var ev = new CalendarEvent { EventPresetId = preset.Id, Source = "Site" };
                ApplyPresetCardFields(ev, preset, target);
                _db.CalendarEvents.Add(ev);
            }

            if (added + revived + updated + removed > 0)
                await _db.SaveChangesAsync(ct);

            CalendarTrace.Write("PULL",
                $"preset={preset.Id} reconcile desired={desired.Count} " +
                $"added={added} revived={revived} updated={updated} removed={removed}" +
                (dropped.Count > 0 ? $" dropped=[{string.Join(", ", dropped)}]" : ""));
        }

        private static void ApplyPresetCardFields(
            CalendarEvent ev, EventPreset preset, SeriesReconciler.DesiredOccurrence target)
        {
            ev.Title = preset.DefaultTitle;
            ev.Description = preset.DefaultDescription;
            ev.Location = preset.Location;
            ev.IsHidden = false;
            ev.OriginalStartUtc = target.OriginalStartUtc.HasValue
                ? Utc(target.OriginalStartUtc.Value)
                : null;
            ev.StartUtc = Utc(target.StartUtc);
            ev.EndUtc = DateTime.SpecifyKind(target.StartUtc.AddHours(preset.DurationHours), DateTimeKind.Utc);
            ev.SkipDiscordSync = !preset.CreateInDiscord;
        }

        private static DateTime Utc(DateTime value)
            => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static DiscordRecurrenceRule? ParseSeriesRule(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var rule = new DiscordRecurrenceRule
                {
                    Frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2,
                    Interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1,
                    Start = root.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? "" : ""
                };
                if (root.TryGetProperty("by_weekday", out var w) && w.ValueKind == JsonValueKind.Array)
                    rule.ByWeekday = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                return string.IsNullOrEmpty(rule.Start) ? null : rule;
            }
            catch { return null; }
        }

        private static List<PresetExceptionRecord> ParseSeriesExceptions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                return (JsonSerializer.Deserialize<List<PresetExceptionRecord>>(json) ?? new())
                    .Where(r => r.OriginalStartUtc > DateTime.UnixEpoch)
                    .ToList();
            }
            catch { return new(); }
        }

        private static bool SameExceptions(string? stored, List<PresetExceptionRecord> actual)
        {
            var old = ParseSeriesExceptions(stored);
            if (old.Count != actual.Count) return false;
            string Key(PresetExceptionRecord e) =>
                $"{e.OriginalStartUtc:O}|{e.IsCanceled}|{e.NewStartUtc:O}";
            return old.Select(Key).OrderBy(k => k).SequenceEqual(actual.Select(Key).OrderBy(k => k));
        }

        private static string Truncate(string? value, int max = 300)
            => string.IsNullOrEmpty(value) ? "-" : value.Length <= max ? value : value[..max] + "...";

        private static string BuildSnapshot(CalendarEvent ev)
        {
            return $"{ev.Title}|{ev.StartUtc:O}|{ev.EndUtc:O}|{ev.Location}|{ev.Description}";
        }

        private static string BuildSnapshotFromRaw(DiscordEventRawInfo de)
        {
            return $"{de.Name}|{de.StartUtc:O}|{de.EndUtc:O}|{de.ChannelId}|{de.Description}";
        }
    }
}