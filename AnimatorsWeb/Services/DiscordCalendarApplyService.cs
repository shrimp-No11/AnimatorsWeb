using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AnimatorsWeb.Services
{
    public class DiscordCalendarApplyService
    {
        private readonly AppDbContext _db;
        private readonly DiscordCalendarSyncService _sync;
        private readonly ILogger<DiscordCalendarApplyService> _logger;

        private readonly DiscordRecurringEventClient _recurringClient;

        public DiscordCalendarApplyService(
            AppDbContext db,
            DiscordCalendarSyncService sync,
            DiscordRecurringEventClient recurringClient,
            ILogger<DiscordCalendarApplyService> logger)
        {
            _db = db;
            _sync = sync;
            _recurringClient = recurringClient;
            _logger = logger;
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

            IReadOnlyList<DiscordScheduledGuildEvent> discordEvents;
            try { discordEvents = await guild.GetEventsAsync(); }
            catch { return plan; }

            var localEvents = await _db.CalendarEvents.ToListAsync(ct);

            var localById = localEvents
                .Where(e => e.DiscordEventId.HasValue)
                .ToDictionary(e => e.DiscordEventId!.Value);

            var discordById = discordEvents.ToDictionary(e => e.Id);

            var localWithoutDiscord = localEvents
                .Where(e => !e.DiscordEventId.HasValue
                         && !e.SkipDiscordSync
                         && !string.IsNullOrEmpty(e.Location))
                .ToList();

            foreach (var ev in localWithoutDiscord)
            {
                plan.ToCreate++;
                plan.Items.Add(new ApplyPlanItem
                {
                    Action = "create",
                    Title = ev.Title,
                    StartUtc = ev.StartUtc,
                    IsRecurring = false
                });
            }

            var now = DateTime.UtcNow;

            foreach (var kv in localById)
            {

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

                bool needsUpdate = false;

                // Проверяем recurring через raw REST (DSharpPlus не отдаёт recurrence_rule)
                try
                {
                    var raw = await _recurringClient.GetEventAsync(guild.Id, kv.Key, ct);
                    var discordIsRecurring = raw?.IsRecurring ?? false;
                    if (kv.Value.IsRecurring != discordIsRecurring)
                        needsUpdate = true;
                }
                catch { }

                // Snapshot (для не-recurring полей)
                if (!needsUpdate)
                {
                    var localSnap = BuildSnapshot(kv.Value);
                    var discordSnap = BuildSnapshotFromDiscord(de);
                    if (localSnap != discordSnap) needsUpdate = true;
                }

                if (needsUpdate)
                {
                    plan.ToUpdate++;
                    plan.Items.Add(new ApplyPlanItem
                    {
                        Action = "update",
                        Title = kv.Value.Title,
                        StartUtc = kv.Value.StartUtc,
                        IsRecurring = kv.Value.IsRecurring
                    });
                }
            }

            var localIds = localById.Keys.ToHashSet();
            foreach (var de in discordEvents)
            {
                if (de.Status == DiscordScheduledGuildEventStatus.Completed ||
                    de.Status == DiscordScheduledGuildEventStatus.Cancelled)
                    continue;

                if (!localIds.Contains(de.Id) && de.StartTime.UtcDateTime > now)
                {
                    plan.ToDelete++;
                    plan.Items.Add(new ApplyPlanItem
                    {
                        Action = "delete",
                        Title = de.Name ?? "(без названия)",
                        StartUtc = de.StartTime.UtcDateTime,
                        IsRecurring = false
                    });
                }
            }

            return plan;
        }

        public async Task<ApplyPlan> ApplyAsync(CancellationToken ct = default)
        {
            var plan = await PreviewAsync(ct);

            var guild = _sync.GetGuildForApply();
            if (guild == null) return plan;

            var localEvents = await _db.CalendarEvents.ToListAsync(ct);
            var guildId = guild.Id;

            // 1) Recurring — через raw REST
            foreach (var ev in localEvents.Where(e => e.IsRecurring && !e.SkipDiscordSync && !string.IsNullOrEmpty(e.Location)))
            {
                try
                {
                    var rule = ParseRule(ev.RecurrenceRuleJson, ev.StartUtc);
                    if (rule == null) continue;

                    bool isChannel = ulong.TryParse(ev.Location, out var channelId);
                    ulong? channel = isChannel ? channelId : null;

                    if (ev.DiscordEventId == null)
                    {
                        var newId = await _recurringClient.CreateRecurringAsync(
                            guildId, channel, ev.Title, ev.Description,
                            ev.StartUtc, ev.EndUtc, rule, ct);

                        if (!string.IsNullOrEmpty(newId) && ulong.TryParse(newId, out var parsed))
                        {
                            ev.DiscordEventId = parsed;
                            ev.Source = "Site";

                            var exceptions = ParseExceptionsJson(ev.RecurrenceExceptionsJson);
                            foreach (var ex in exceptions)
                            {
                                await _recurringClient.AddExceptionAsync(guildId, parsed, ex, true, ct);
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
                            ev.StartUtc, ev.EndUtc, rule, ct);
                        if (ok)
                        {
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

            // 2) Обычные — через raw REST (PushAsync переписан выше)
            foreach (var ev in localEvents.Where(e => !e.IsRecurring && !e.SkipDiscordSync && !string.IsNullOrEmpty(e.Location)))
            {
                try
                {
                    await _sync.PushAsync(ev, ct);
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Push failed for event {Id}", ev.Id);
                }
                await Task.Delay(200, ct);
            }

            // 3) Удаления
            var discordEvents = await guild.GetEventsAsync();
            var localIds = localEvents
                .Where(e => e.DiscordEventId.HasValue)
                .Select(e => e.DiscordEventId!.Value)
                .ToHashSet();

            var now = DateTime.UtcNow;
            foreach (var de in discordEvents)
            {
                if (de.Status == DiscordScheduledGuildEventStatus.Completed ||
                    de.Status == DiscordScheduledGuildEventStatus.Cancelled)
                    continue;

                if (!localIds.Contains(de.Id) && de.StartTime.UtcDateTime > now)
                {
                    try
                    {
                        var ok = await _recurringClient.DeleteAsync(guildId, de.Id, ct);
                        if (!ok) _logger.LogWarning("Delete failed for Discord event {Id}", de.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Delete failed for Discord event {Id}", de.Id);
                    }
                    await Task.Delay(200, ct);
                }
            }

            _logger.LogInformation(
                "Calendar apply: created {C}, updated {U}, deleted {D}",
                plan.ToCreate, plan.ToUpdate, plan.ToDelete);

            return plan;
        }

        private static string BuildSnapshot(CalendarEvent ev) =>
            $"{ev.Title}|{ev.StartUtc:O}|{ev.EndUtc:O}|{ev.Location}|{ev.Description}";

        private static string BuildSnapshotFromDiscord(DiscordScheduledGuildEvent de) =>
            $"{de.Name}|{de.StartTime.UtcDateTime:O}|" +
            $"{(de.EndTime?.UtcDateTime.ToString("O") ?? "")}|" +
            $"{de.ChannelId}|{de.Description}";


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

        private static List<DateTime> ParseExceptionsJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list == null) return new();
                return list
                    .Select(s => DateTime.TryParse(s, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : (DateTime?)null)
                    .Where(d => d.HasValue)
                    .Select(d => d!.Value)
                    .ToList();
            }
            catch { return new(); }
        }
    }
}