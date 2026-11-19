using System.Text.Json;
using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class DiscordCalendarApplyService
    {
        private readonly AppDbContext _db;
        private readonly DiscordCalendarSyncService _sync;
        private readonly DiscordRecurringEventClient _recurringClient;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<DiscordCalendarApplyService> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpFactory;

        public DiscordCalendarApplyService(
            AppDbContext db,
            DiscordCalendarSyncService sync,
            DiscordRecurringEventClient recurringClient,
            ILoggerFactory loggerFactory,
            ILogger<DiscordCalendarApplyService> logger,
            IWebHostEnvironment env,
            IHttpClientFactory httpFactory)
        {
            _db = db;
            _sync = sync;
            _recurringClient = recurringClient;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _env = env;
            _httpFactory = httpFactory;
        }

        public class ApplyPlan
        {
            public int ToCreate { get; set; }
            public int ToUpdate { get; set; }
            public int ToDelete { get; set; }
            public List<ApplyPlanItem> Items { get; set; } = new();
        }

        public class ApplyPlanItem
        {
            public string Action { get; set; } = "";
            public string Title { get; set; } = "";
            public DateTime StartUtc { get; set; }
            public bool IsRecurring { get; set; }
        }

        public async Task<ApplyPlan> PreviewAsync(CancellationToken ct = default)
        {
            var plan = new ApplyPlan();

            var guild = _sync.GetGuildForApply();
            if (guild == null) return plan;

            // DSharpPlus с первого вызова может вернуть пустой/неполный список событий —
            // ходим через raw REST, где есть ретрай 429, и делаем одну повторную попытку.
            var rawMap = await _recurringClient.GetAllEventsRawAsync(guild.Id, ct);
            if (rawMap.Count == 0)
            {
                await Task.Delay(300, ct);
                rawMap = await _recurringClient.GetAllEventsRawAsync(guild.Id, ct);
            }

            var allPresets = await _db.EventPresets.ToListAsync(ct);
            var allSeriesPresets = allPresets.Where(p => p.IsDiscordSeries).ToList();

            var seriesPresetIdSet = allSeriesPresets
                .Where(p => !p.IsDeleted)
                .Select(p => p.Id)
                .ToHashSet();

            var localEvents = await _db.CalendarEvents.Where(e => !e.IsHidden).ToListAsync(ct);

            var localById = localEvents
                .Where(e => e.DiscordEventId.HasValue)
                .ToDictionary(e => e.DiscordEventId!.Value);

            var discordById = rawMap;

            var localWithoutDiscord = localEvents
                .Where(e => !e.DiscordEventId.HasValue
                         && !e.SkipDiscordSync
                         && !string.IsNullOrEmpty(e.Location)
                         && !IsSeriesPresetEvent(e.EventPresetId, seriesPresetIdSet))
                .ToList();

            foreach (var ev in localWithoutDiscord)
            {
                plan.ToCreate++;
                plan.Items.Add(new ApplyPlanItem
                {
                    Action = "create",
                    Title = ev.Title,
                    StartUtc = ev.StartUtc,
                    IsRecurring = ev.IsRecurring
                });
            }

            var now = DateTime.UtcNow;

            foreach (var kv in localById)
            {
                if (IsSeriesPresetEvent(kv.Value.EventPresetId, seriesPresetIdSet)) continue;

                if (kv.Value.IsRecurring)
                {
                    if (!discordById.ContainsKey(kv.Key))
                    {
                        if (kv.Value.StartUtc > now)
                        {
                            plan.ToCreate++;
                            plan.Items.Add(new ApplyPlanItem
                            {
                                Action = "create", Title = kv.Value.Title,
                                StartUtc = kv.Value.StartUtc, IsRecurring = true
                            });
                        }
                        continue;
                    }

                    // У DSharpPlus нет recurrence_rule/exceptions — читаем сырое событие.
                    var raw = await _recurringClient.GetEventAsync(guild.Id, kv.Key, ct);
                    if (raw == null) continue;

                    bool changed = raw.Name != kv.Value.Title
                        || raw.Description != kv.Value.Description
                        || raw.StartUtc != kv.Value.StartUtc
                        || raw.EndUtc != kv.Value.EndUtc
                        || raw.ChannelId?.ToString() != kv.Value.Location
                        || !DiscordRuleJson.Same(kv.Value.RecurrenceRuleJson, raw.RecurrenceRuleJson);

                    if (!changed)
                    {
                        var desired = EventExceptionJson.Parse(kv.Value.RecurrenceExceptionsJson);
                        var discordByDate = raw.Exceptions
                            .GroupBy(x => x.OriginalStartUtc)
                            .ToDictionary(g => g.Key, g => g.First());
                        var desiredDates = desired.Select(x => x.OriginalStartUtc).ToHashSet();
                        changed = desired.Any(d =>
                                      !discordByDate.TryGetValue(d.OriginalStartUtc, out var current)
                                      || SeriesExceptionDiff.NeedsUpdate(current, d))
                                   || discordByDate.Keys.Any(k => !desiredDates.Contains(k));
                    }

                    if (changed)
                    {
                        plan.ToUpdate++;
                        plan.Items.Add(new ApplyPlanItem
                        {
                            Action = "update", Title = kv.Value.Title,
                            StartUtc = kv.Value.StartUtc, IsRecurring = true
                        });
                    }
                    continue;
                }


                if (!discordById.TryGetValue(kv.Key, out var de))
                {
                    if (kv.Value.StartUtc > now)
                    {
                        plan.ToCreate++;
                        plan.Items.Add(new ApplyPlanItem
                        {
                            Action = "create",
                            Title = kv.Value.Title,
                            StartUtc = kv.Value.StartUtc,
                            IsRecurring = false
                        });
                    }
                    continue;
                }

                var localSnap = BuildSnapshot(kv.Value);
                var discordSnap = BuildSnapshotFromRaw(de);
                if (localSnap != discordSnap)
                {
                    plan.ToUpdate++;
                    plan.Items.Add(new ApplyPlanItem
                    {
                        Action = "update",
                        Title = kv.Value.Title,
                        StartUtc = kv.Value.StartUtc,
                        IsRecurring = false
                    });
                }
            }

            var localIds = localById.Keys.ToHashSet();

            foreach (var p in allSeriesPresets.Where(p => !p.IsDeleted && p.DiscordSeriesEventId != null))
                localIds.Add(p.DiscordSeriesEventId!.Value);

            // Удалённые пресеты (серии и одиночные события) → удаление из Discord
            foreach (var p in allPresets.Where(p => p.IsDeleted))
            {
                plan.ToDelete++;
                plan.Items.Add(new ApplyPlanItem
                {
                    Action = "delete",
                    Title = $"{(p.IsDiscordSeries ? "Серия: " : "")}{p.DefaultTitle} (пресет удалён)",
                    StartUtc = DateTime.UtcNow,
                    IsRecurring = p.IsDiscordSeries
                });
            }

            // Новые серии (создать)
            var newSeriesPresets = allSeriesPresets
                .Where(p => !p.IsDeleted && p.DiscordSeriesEventId == null)
                .ToList();

            foreach (var preset in newSeriesPresets)
            {
                var calc = await BuildSeriesRuleAsync(preset, now, ct);
                if (calc == null) continue;

                plan.ToCreate++;
                plan.Items.Add(new ApplyPlanItem
                {
                    Action = "create",
                    Title = preset.DefaultTitle,
                    StartUtc = calc.RuleStartUtc,
                    IsRecurring = true
                });
            }

            // Изменения существующих серий: сдвинутые карточки, смена времени/дня,
            // новые или снятые исключения — всё это должно светиться в preview.
            foreach (var preset in allSeriesPresets
                         .Where(p => !p.IsDeleted && p.DiscordSeriesEventId != null))
            {
                var calc = await BuildSeriesRuleAsync(preset, now, ct);
                if (calc == null) continue;

                var currentRule = new DiscordRecurrenceRule
                {
                    Start = calc.RuleStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Frequency = calc.Frequency,
                    Interval = calc.Interval,
                    ByWeekday = calc.ByWeekday
                };

                // Сравниваем с ФАКТИЧЕСКИМ состоянием Discord, а не с локальным кэшем:
                // локальные правки сразу записываются в пресет, поэтому сравнение
                // «пресет vs карточки» всегда показывало «изменений нет».
                var raw = await _recurringClient.GetEventAsync(
                    guild.Id, preset.DiscordSeriesEventId!.Value, ct);
                if (raw == null) continue;

                bool changed = RuleChanged(raw.RecurrenceRuleJson, currentRule);
                if (!changed)
                {
                    var desiredExceptions = SeriesExceptionDiff.Merge(
                        calc.Exceptions, ParsePresetExceptions(preset.RecurrenceExceptionsJson),
                        calc.Occurrences, now);

                    var discordByDate = raw.Exceptions
                        .GroupBy(x => x.OriginalStartUtc)
                        .ToDictionary(g => g.Key, g => g.First());
                    var desiredDates = desiredExceptions
                        .Select(x => x.OriginalStartUtc)
                        .ToHashSet();

                    changed = desiredExceptions.Any(d =>
                                  !discordByDate.TryGetValue(d.OriginalStartUtc, out var current)
                                  || SeriesExceptionDiff.NeedsUpdate(current, d))
                              || discordByDate.Keys.Any(k => !desiredDates.Contains(k));
                }

                if (changed)
                {
                    // В сводке показываем то вхождение, которое реально сдвинули/отменили,
                    // а не всегда первое в серии.
                    var changedStart = calc.Exceptions.FirstOrDefault(e => !e.IsCanceled)?.NewStartUtc
                                       ?? calc.Exceptions.FirstOrDefault(e => e.IsCanceled)?.OriginalStartUtc
                                       ?? calc.RuleStartUtc;
                    plan.ToUpdate++;
                    plan.Items.Add(new ApplyPlanItem
                    {
                        Action = "update",
                        Title = preset.DefaultTitle,
                        StartUtc = changedStart,
                        IsRecurring = true
                    });
                }
            }

            // Discord-события, отсутствующие в сетке, НЕ удаляем: карточка может быть снята с сетки,
            // но событие в Discord продолжает жить, пока не удалён его пресет.

            return plan;
        }

        public async Task<ApplyPlan> ApplyAsync(CancellationToken ct = default)
        {
            var plan = await PreviewAsync(ct);

            var guild = _sync.GetGuildForApply();
            if (guild == null) return plan;

            var guildId = guild.Id;

            var seriesPresetIdSet = (await _db.EventPresets
                .Where(p => p.IsDiscordSeries && !p.IsDeleted)
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();

            var localEvents = await _db.CalendarEvents.Where(e => !e.IsHidden).ToListAsync(ct);
            var presetById = await _db.EventPresets.ToDictionaryAsync(p => p.Id, ct);

            // 1) Удалённые пресеты = удаление события/серии из Discord + финальная уборка БД
            var deletedPresets = await _db.EventPresets
                .Where(p => p.IsDeleted)
                .ToListAsync(ct);

            foreach (var preset in deletedPresets)
            {
                try
                {
                    var idsToDelete = new HashSet<ulong>();
                    if (preset.DiscordSeriesEventId.HasValue)
                        idsToDelete.Add(preset.DiscordSeriesEventId.Value);

                    var cards = await _db.CalendarEvents
                        .Where(e => e.EventPresetId == preset.Id)
                        .ToListAsync(ct);
                    foreach (var id in cards.Where(e => e.DiscordEventId.HasValue)
                                 .Select(e => e.DiscordEventId!.Value))
                        idsToDelete.Add(id);

                    foreach (var id in idsToDelete)
                    {
                        await _recurringClient.DeleteAsync(guildId, id, ct);
                        await Task.Delay(200, ct);
                    }

                    _db.CalendarEvents.RemoveRange(cards);
                    _db.EventPresets.Remove(preset);
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deleted preset cleanup failed for preset {Id}", preset.Id);
                }
            }

            // 2) Серии-пресеты
            var seriesPresets = await _db.EventPresets
                .Where(p => p.IsDiscordSeries && !p.IsDeleted)
                .ToListAsync(ct);

            foreach (var preset in seriesPresets)
            {
                try
                {
                    var calc = await BuildSeriesRuleAsync(preset, DateTime.UtcNow, ct);

                    if (calc == null)
                    {
                        if (preset.DiscordSeriesEventId != null && !string.IsNullOrEmpty(preset.RecurrenceRuleJson))
                        {
                            await SyncSuspendedSeriesAsync(guildId, preset, ct);
                            preset.PendingApply = false;
                            await _db.SaveChangesAsync(ct);
                            await Task.Delay(200, ct);
                        }
                        continue;
                    }

                    var seriesEndUtc = calc.RuleStartUtc.AddHours(preset.DurationHours);
                    var synced = await SyncSeriesAsync(guildId, preset, calc, seriesEndUtc, ct);
                    if (synced)
                    {
                        preset.PendingApply = false;
                        await _db.SaveChangesAsync(ct);

                        // Приводим карточки к только что отправленному состоянию:
                        // gateway мог успеть перечитать промежуточное состояние серии.
                        await _sync.ReconcileOwnedSeriesAsync(preset, ct);
                        await _db.SaveChangesAsync(ct);
                    }
                    await Task.Delay(200, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Series sync failed for preset {Id}", preset.Id);
                }
            }

            // 2) Одиночные recurring
            foreach (var ev in localEvents.Where(e => e.IsRecurring
                    && !e.SkipDiscordSync
                    && !string.IsNullOrEmpty(e.Location)
                    && !IsSeriesPresetEvent(e.EventPresetId, seriesPresetIdSet)))
            {
                try
                {
                    var rule = ParseRule(ev.RecurrenceRuleJson, ev.StartUtc);
                    if (rule == null) continue;

                    bool isChannel = ulong.TryParse(ev.Location, out var channelId);
                    ulong? channel = isChannel ? channelId : null;
                    var preset = ev.EventPresetId.HasValue && presetById.TryGetValue(ev.EventPresetId.Value, out var p)
                        ? p : null;
                    var cover = await GetCoverDataUriAsync(preset, ct);
                    var localExceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);

                    // Ничего не менялось — не дёргаем Discord (иначе Apply на сотнях событий долгий).
                    if (ev.DiscordEventId != null && !string.IsNullOrEmpty(ev.DiscordSnapshot)
                        && ev.DiscordSnapshot == BuildSnapshot(ev) && localExceptions.Count == 0)
                        continue;

                    if (ev.DiscordEventId == null)
                    {
                        var newId = await _recurringClient.CreateRecurringAsync(
                            guildId, channel, ev.Title, ev.Description,
                            ev.StartUtc, ev.EndUtc, rule, ct, cover);

                        if (!string.IsNullOrEmpty(newId) && ulong.TryParse(newId, out var parsed))
                        {
                            ev.DiscordEventId = parsed;
                            ev.Source = "Site";

                            var exceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                            foreach (var ex in exceptions)
                            {
                                await _recurringClient.AddExceptionAsync(
                                    guildId, parsed, ex.OriginalStartUtc, ex.IsCanceled, ex.NewStartUtc, ct,
                                    ex.NewStartUtc.HasValue ? ex.NewStartUtc.Value + (ev.EndUtc - ev.StartUtc) : null);
                                await Task.Delay(150, ct);
                            }

                            ev.DiscordSnapshot = BuildSnapshot(ev);
                            ev.LastSyncedUtc = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        var ok = await _recurringClient.ModifyRecurringAsync(
                            guildId, ev.DiscordEventId.Value, ev.Title, ev.Description,
                            ev.StartUtc, ev.EndUtc, rule, ct, imageDataUri: cover);
                        if (ok)
                        {
                            // PATCH с recurrence_rule мог сбросить исключения — восстанавливаем их.
                            // Отменённые вхождения хранятся как список дат в RecurrenceExceptionsJson.
                            var desiredExceptions = EventExceptionJson.Parse(ev.RecurrenceExceptionsJson);
                            var desiredDates = desiredExceptions
                                .Select(x => x.OriginalStartUtc).ToHashSet();
                            var discordExceptions = await _recurringClient.GetExceptionsFromEventAsync(
                                guildId, ev.DiscordEventId.Value, ct);
                            var discordByDate = discordExceptions
                                .GroupBy(x => x.OriginalStartUtc)
                                .ToDictionary(g => g.Key, g => g.First());

                            foreach (var desired in desiredExceptions)
                            {
                                if (discordByDate.TryGetValue(desired.OriginalStartUtc, out var current)
                                    && !SeriesExceptionDiff.NeedsUpdate(current, desired, ruleChanged: true))
                                    continue;
                                await _recurringClient.AddExceptionAsync(
                                    guildId, ev.DiscordEventId.Value,
                                    desired.OriginalStartUtc, desired.IsCanceled, desired.NewStartUtc, ct,
                                    desired.NewStartUtc.HasValue
                                        ? desired.NewStartUtc.Value + (ev.EndUtc - ev.StartUtc) : null);
                                await Task.Delay(150, ct);
                            }
                            foreach (var kv in discordByDate.Where(kv => !desiredDates.Contains(kv.Key)))
                            {
                                if (!string.IsNullOrEmpty(kv.Value.ExceptionId))
                                {
                                    await _recurringClient.RemoveExceptionAsync(
                                        guildId, ev.DiscordEventId.Value, kv.Value.ExceptionId, ct);
                                    await Task.Delay(150, ct);
                                }
                            }

                            ev.DiscordSnapshot = BuildSnapshot(ev);
                            ev.LastSyncedUtc = DateTime.UtcNow;
                        }
                    }

                    await _db.SaveChangesAsync(ct);
                    await Task.Delay(200, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Recurring push failed for event {Id}", ev.Id);
                }
            }

            // 3) Обычные одиночные
            foreach (var ev in localEvents.Where(e => !e.IsRecurring
                    && !e.SkipDiscordSync
                    && !string.IsNullOrEmpty(e.Location)
                    && !IsSeriesPresetEvent(e.EventPresetId, seriesPresetIdSet)))
            {
                try
                {
                    var preset = ev.EventPresetId.HasValue && presetById.TryGetValue(ev.EventPresetId.Value, out var p)
                        ? p : null;
                    await _sync.PushAsync(ev, ct, await GetCoverDataUriAsync(preset, ct));
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Push failed for event {Id}", ev.Id);
                }
                await Task.Delay(200, ct);
            }

            // 4) Удаление «missing» убрано намеренно: снятие карточки с сетки не означает
            //    удаление события из Discord. Удаляет только удаление пресета (шаг 1).

            _logger.LogInformation(
                "Calendar apply: created {C}, updated {U}, deleted {D}",
                plan.ToCreate, plan.ToUpdate, plan.ToDelete);

            // Другие открытые страницы календаря перезагрузятся и увидят результат.
            CalendarChangeNotifier.Bump();
            return plan;
        }

        private static bool IsSeriesPresetEvent(int? presetId, HashSet<int> seriesPresetIds)
            => presetId.HasValue && seriesPresetIds.Contains(presetId.Value);

        /// <summary>Обложка пресета в формате Discord image data (data URI).</summary>
        private async Task<string?> GetCoverDataUriAsync(EventPreset? preset, CancellationToken ct)
        {
            if (preset == null) return null;
            byte[]? bytes = null;
            string mime = "image/jpeg";

            if (!string.IsNullOrWhiteSpace(preset.CoverImageFileName))
            {
                var path = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", "covers", preset.CoverImageFileName);
                if (System.IO.File.Exists(path)) bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
                mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    _ => "image/jpeg"
                };
            }
            else if (!string.IsNullOrWhiteSpace(preset.CoverImageUrl))
            {
                try
                {
                    var http = _httpFactory.CreateClient();
                    bytes = await http.GetByteArrayAsync(preset.CoverImageUrl, ct);
                    mime = preset.CoverImageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                        : preset.CoverImageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                        : preset.CoverImageUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
                        : "image/jpeg";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download cover for preset {Id}", preset.Id);
                }
            }

            return bytes == null ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        private static string Truncate(string? value, int max = 300)
            => string.IsNullOrEmpty(value) ? "-" : value.Length <= max ? value : value[..max] + "...";

        private static string SerializeRule(DiscordRecurrenceRule rule) => JsonSerializer.Serialize(new
        {
            start = rule.Start,
            frequency = rule.Frequency,
            interval = rule.Interval,
            by_weekday = rule.ByWeekday
        });

        /// <summary>
        /// PATCH с recurrence_rule может сбросить все исключения серии в Discord.
        /// Отправляем rule только когда он реально изменился, чтобы не тревожить подписки.
        /// </summary>
        private static bool RuleChanged(string? storedJson, DiscordRecurrenceRule rule)
        {
            if (string.IsNullOrWhiteSpace(storedJson)) return true;
            try
            {
                using var doc = JsonDocument.Parse(storedJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("start", out var s)
                    || s.ValueKind != JsonValueKind.String
                    || !DateTime.TryParse(s.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var storedStart)
                    || !DateTime.TryParse(rule.Start, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var newStart)
                    || DateTime.SpecifyKind(storedStart, DateTimeKind.Utc) != DateTime.SpecifyKind(newStart, DateTimeKind.Utc))
                    return true;
                if (!root.TryGetProperty("frequency", out var f) || f.GetInt32() != rule.Frequency) return true;
                if (!root.TryGetProperty("interval", out var i) || i.GetInt32() != rule.Interval) return true;
                if (!root.TryGetProperty("by_weekday", out var w) || w.ValueKind != JsonValueKind.Array) return true;
                var storedDays = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                return !storedDays.SequenceEqual(rule.ByWeekday ?? Array.Empty<int>());
            }
            catch { return true; }
        }

        private async Task<RecurrenceCalculation?> BuildSeriesRuleAsync(
    EventPreset preset, DateTime now, CancellationToken ct)
        {
            var occurrences = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id
                         && !e.IsHidden
                         && e.StartUtc > now.AddDays(-1))
                .OrderBy(e => e.StartUtc)
                .Select(e => new { e.StartUtc, e.EndUtc, e.OriginalStartUtc })
                .ToListAsync(ct);

            if (occurrences.Count == 0) return null;

            // Горизонт: минимум 13 недель вперёд (как у автопродления), чтобы Discord
            // не показывал «лишние» вхождения, которых нет на сетке.
            var horizon = occurrences.Max(o => o.StartUtc).AddDays(7);
            var rollingHorizon = now.AddDays(SeriesOccurrenceExtender.HorizonWeeks * 7);
            if (rollingHorizon > horizon) horizon = rollingHorizon;

            var cards = occurrences
                .Select(o => (o.StartUtc, o.OriginalStartUtc))
                .ToList();

            return SeriesRulePlanner.Build(preset, cards, horizon);
        }

        private static List<PresetExceptionRecord> ParsePresetExceptions(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            try
            {
                return (JsonSerializer.Deserialize<List<PresetExceptionRecord>>(json) ?? new())
                    .Where(r => r.OriginalStartUtc > DateTime.UnixEpoch)
                    .ToList();
            }
            catch { return new(); }
        }

        private static string SerializePresetExceptions(List<PresetExceptionRecord> list)
            => JsonSerializer.Serialize(list);

        private static bool SameExceptions(string? stored, List<PresetExceptionRecord> desired)
        {
            var old = ParsePresetExceptions(stored);
            if (old.Count != desired.Count) return false;
            string Key(PresetExceptionRecord e) =>
                $"{e.OriginalStartUtc:O}|{e.IsCanceled}|{e.NewStartUtc:O}";
            return old.Select(Key).OrderBy(k => k)
                .SequenceEqual(desired.Select(Key).OrderBy(k => k));
        }

        private async Task<bool> SyncSeriesAsync(
    ulong guildId,
    EventPreset preset,
    RecurrenceCalculation calc,
    DateTime seriesEndUtc,
    CancellationToken ct)
        {
            var rule = new DiscordRecurrenceRule
            {
                Start = calc.RuleStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Frequency = calc.Frequency,
                Interval = calc.Interval,
                ByWeekday = calc.ByWeekday
            };

            var ruleJson = SerializeRule(rule);
            bool ruleChanged = RuleChanged(preset.RecurrenceRuleJson, rule);
            CalendarTrace.Write("APPLY", $"preset={preset.Id} series={preset.DiscordSeriesEventId} " +
                $"rule={ruleJson} changed={ruleChanged} occ={calc.Occurrences.Count} " +
                $"ex={calc.Exceptions.Count} unmatched={calc.UnmatchedOccurrences.Count}");
            if (calc.UnmatchedOccurrences.Count > 0)
            {
                _logger.LogWarning(
                    "Series preset {PresetId}: {Count} occurrences do not match the computed rule and cannot be represented in Discord",
                    preset.Id, calc.UnmatchedOccurrences.Count);
            }

            ulong seriesId;
            var cover = await GetCoverDataUriAsync(preset, ct);

            if (preset.DiscordSeriesEventId == null)
            {
                bool isChannel = ulong.TryParse(preset.Location, out var chId);
                ulong? channel = isChannel ? chId : null;
                var newId = await _recurringClient.CreateRecurringAsync(
                    guildId, channel, preset.DefaultTitle, preset.DefaultDescription,
                    calc.RuleStartUtc, seriesEndUtc, rule, ct, cover);

                if (string.IsNullOrEmpty(newId) || !ulong.TryParse(newId, out var parsed))
                {
                    _logger.LogWarning("Create series failed for preset {PresetId}", preset.Id);
                    return false;
                }
                seriesId = parsed;
                preset.DiscordSeriesEventId = parsed;

                // Пресет мог раньше жить как обычное событие: серия его заменяет,
                // иначе в Discord останется дубликат.
                var standalone = await _db.CalendarEvents
                    .Where(e => e.EventPresetId == preset.Id && e.DiscordEventId != null)
                    .ToListAsync(ct);
                foreach (var old in standalone)
                {
                    try
                    {
                        await _recurringClient.DeleteAsync(guildId, old.DiscordEventId!.Value, ct);
                        await Task.Delay(150, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete standalone event {Id} of preset {PresetId}",
                            old.DiscordEventId, preset.Id);
                    }

                    old.DiscordEventId = null;
                    old.DiscordSnapshot = null;
                    old.IsRecurring = false;
                    old.RecurrenceRuleJson = null;
                    old.RecurrenceExceptionsJson = null;
                }
                if (standalone.Count > 0) await _db.SaveChangesAsync(ct);
            }
            else
            {
                seriesId = preset.DiscordSeriesEventId.Value;
                // Не шлём rule без изменений: Discord может снести все исключения серии.
                var ok = await _recurringClient.ModifyRecurringAsync(
                    guildId, seriesId, preset.DefaultTitle, preset.DefaultDescription,
                    calc.RuleStartUtc, seriesEndUtc, rule, ct, includeRule: ruleChanged,
                    imageDataUri: cover);
                if (!ok)
                {
                    _logger.LogWarning("Modify series failed for preset {PresetId}", preset.Id);
                    return false;
                }
            }

            var previous = ParsePresetExceptions(preset.RecurrenceExceptionsJson);
            var desiredExceptions = SeriesExceptionDiff.Merge(
                calc.Exceptions, previous, calc.Occurrences, DateTime.UtcNow);
            var desiredDates = desiredExceptions.Select(e => e.OriginalStartUtc).ToHashSet();
            int failures = 0;

            var discordExceptions = await _recurringClient.GetExceptionsFromEventAsync(guildId, seriesId, ct);
            var discordByDate = discordExceptions
                .GroupBy(e => e.OriginalStartUtc)
                .ToDictionary(g => g.Key, g => g.First());
            CalendarTrace.Write("APPLY", $"preset={preset.Id} exceptions desired={desiredExceptions.Count} " +
                $"discord={discordByDate.Count} desiredJson={Truncate(preset.RecurrenceExceptionsJson)}");

            foreach (var desired in desiredExceptions)
            {
                // POST с тем же original_scheduled_start_time полностью перезаписывает исключение.
                // Это меняет тип (отмена <-> перенос) и новое время без удаления/создания серии.
                if (discordByDate.TryGetValue(desired.OriginalStartUtc, out var current)
                    && !SeriesExceptionDiff.NeedsUpdate(current, desired, ruleChanged))
                    continue;

                var created = await _recurringClient.AddExceptionAsync(
                    guildId, seriesId, desired.OriginalStartUtc,
                    desired.IsCanceled, desired.NewStartUtc, ct,
                    desired.NewStartUtc.HasValue
                        ? desired.NewStartUtc.Value.AddHours(preset.DurationHours) : null);
                if (created == null) failures++;
                await Task.Delay(150, ct);
            }

            foreach (var kv in discordByDate)
            {
                if (desiredDates.Contains(kv.Key)) continue;
                if (!string.IsNullOrEmpty(kv.Value.ExceptionId))
                {
                    if (!await _recurringClient.RemoveExceptionAsync(guildId, seriesId, kv.Value.ExceptionId, ct))
                        failures++;
                    await Task.Delay(150, ct);
                }
            }

            preset.RecurrenceRuleJson = ruleJson;
            preset.RecurrenceExceptionsJson = SerializePresetExceptions(desiredExceptions);

            if (failures > 0)
            {
                // Не снимаем PendingApply: иначе следующий Pull решит, что Discord
                // — истина, и удалит карточки, которые не успели доехать.
                CalendarTrace.Write("APPLY",
                    $"preset={preset.Id} exceptions failed={failures} — keep pending");
                return false;
            }

            return true;
        }

        private static DiscordRecurrenceRule? ParseRule(string? json, DateTime startUtc)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var rule = new DiscordRecurrenceRule
                {
                    Start = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
                if (root.TryGetProperty("frequency", out var f)) rule.Frequency = f.GetInt32();
                if (root.TryGetProperty("interval", out var i)) rule.Interval = i.GetInt32();
                if (root.TryGetProperty("by_weekday", out var b) && b.ValueKind == JsonValueKind.Array)
                    rule.ByWeekday = b.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                return rule;
            }
            catch { return null; }
        }

        private async Task SyncSuspendedSeriesAsync(
    ulong guildId, EventPreset preset, CancellationToken ct)
        {
            if (preset.DiscordSeriesEventId == null || string.IsNullOrEmpty(preset.RecurrenceRuleJson))
                return;

            DateTime ruleStart;
            try
            {
                using var doc = JsonDocument.Parse(preset.RecurrenceRuleJson);
                if (!doc.RootElement.TryGetProperty("start", out var startEl)
                    || startEl.ValueKind != JsonValueKind.String
                    || !DateTime.TryParse(startEl.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out ruleStart))
                    return;
                ruleStart = DateTime.SpecifyKind(ruleStart, DateTimeKind.Utc);
            }
            catch { return; }

            var rule = ParseRule(preset.RecurrenceRuleJson, ruleStart);
            if (rule == null) return;

            var seriesId = preset.DiscordSeriesEventId.Value;
            var seriesEnd = ruleStart.AddHours(preset.DurationHours);

            var ok = await _recurringClient.ModifyRecurringAsync(
                guildId, seriesId, preset.DefaultTitle, preset.DefaultDescription,
                ruleStart, seriesEnd, rule, ct, imageDataUri: await GetCoverDataUriAsync(preset, ct));
            if (!ok) return;

            var cutoff = DateTime.UtcNow.AddDays(-SeriesExceptionDiff.RetentionDays);
            var previous = ParsePresetExceptions(preset.RecurrenceExceptionsJson)
                .Where(p => p.OriginalStartUtc >= cutoff)
                .ToList();
            var previousDates = previous.Select(p => p.OriginalStartUtc).ToHashSet();
            // Старая история больше не нужна ни в БД, ни в Discord.
            preset.RecurrenceExceptionsJson = SerializePresetExceptions(previous);

            var discordExceptions = await _recurringClient.GetExceptionsFromEventAsync(guildId, seriesId, ct);
            var discordByDate = discordExceptions
                .GroupBy(e => e.OriginalStartUtc)
                .ToDictionary(g => g.Key, g => g.First());

            // PATCH rule мог сбросить исключения — переотправляем все (POST перезаписывает).
            foreach (var prev in previous)
            {
                if (discordByDate.TryGetValue(prev.OriginalStartUtc, out var current)
                    && !SeriesExceptionDiff.NeedsUpdate(current, prev, ruleChanged: true))
                    continue;

                await _recurringClient.AddExceptionAsync(
                    guildId, seriesId, prev.OriginalStartUtc,
                    prev.IsCanceled, prev.NewStartUtc, ct,
                    prev.NewStartUtc.HasValue
                        ? prev.NewStartUtc.Value.AddHours(preset.DurationHours) : null);
                await Task.Delay(150, ct);
            }

            foreach (var kv in discordByDate.Where(kv => !previousDates.Contains(kv.Key)))
            {
                if (!string.IsNullOrEmpty(kv.Value.ExceptionId))
                {
                    await _recurringClient.RemoveExceptionAsync(guildId, seriesId, kv.Value.ExceptionId, ct);
                    await Task.Delay(150, ct);
                }
            }
        }

        private static string BuildSnapshot(CalendarEvent ev) =>
            $"{ev.Title}|{ev.StartUtc:O}|{ev.EndUtc:O}|{ev.Location}|{ev.Description}";

        private static string BuildSnapshotFromRaw(DiscordEventRawInfo de) =>
            $"{de.Name}|{de.StartUtc:O}|{de.EndUtc:O}|{de.ChannelId}|{de.Description}";
    }
}