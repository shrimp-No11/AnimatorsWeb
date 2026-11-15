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

        public async Task<ulong?> PushAsync(CalendarEvent ev, CancellationToken ct = default)
        {
            if (ev.SkipDiscordSync) return null;

            var guild = GetGuildForApply();
            if (guild == null) return null;
            if (string.IsNullOrWhiteSpace(ev.Location)) return null;

            bool isChannel = ulong.TryParse(ev.Location, out var channelId);
            ulong? channel = isChannel ? channelId : null;
            string? externalLocation = isChannel ? null : ev.Location;

            try
            {
                if (ev.DiscordEventId == null)
                {
                    var newId = await _recurringClient.CreateSingleAsync(
                        guild.Id, channel, ev.Title, ev.Description,
                        ev.StartUtc, ev.EndUtc, externalLocation, ct);
                    if (string.IsNullOrEmpty(newId) || !ulong.TryParse(newId, out var parsed))
                        return null;
                    ev.DiscordEventId = parsed;
                    ev.Source = "Site";
                }
                else
                {
                    var ok = await _recurringClient.ModifySingleAsync(
                        guild.Id, ev.DiscordEventId.Value, ev.Title, ev.Description,
                        ev.StartUtc, ev.EndUtc, channel, externalLocation, ct);
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

        public async Task PullAsync(CancellationToken ct = default)
        {
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

            var byDiscordId = await _db.CalendarEvents
                .Where(e => e.DiscordEventId != null)
                .ToDictionaryAsync(e => e.DiscordEventId!.Value, ct);

            foreach (var kv in rawMap)
            {
                var de = kv.Value;
                // 3=Completed, 4=Cancelled
                if (de.Status == 3 || de.Status == 4) continue;

                CalendarEvent ev;
                bool changed;

                if (byDiscordId.TryGetValue(de.Id, out var existing))
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
                    ev.RecurrenceExceptionsJson = System.Text.Json.JsonSerializer.Serialize(
                        de.Exceptions.Select(x => x.OriginalStartUtc.ToString("O")));
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

            var now = DateTime.UtcNow;
            var discordIds = rawMap.Keys.ToHashSet();
            var missing = await _db.CalendarEvents
                .Where(e => e.DiscordEventId != null
                         && e.StartUtc > now
                         && !discordIds.Contains(e.DiscordEventId.Value))
                .ToListAsync(ct);

            _db.CalendarEvents.RemoveRange(missing);

            await _db.SaveChangesAsync(ct);
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