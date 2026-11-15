using AnimatorsWeb.Data;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ScheduledEventSyncHandler : IEventHandler<ScheduledGuildEventUpdatedEventArgs>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledEventSyncHandler> _logger;

        public ScheduledEventSyncHandler(
            IServiceScopeFactory scopeFactory,
            ILogger<ScheduledEventSyncHandler> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task HandleEventAsync(DiscordClient sender, ScheduledGuildEventUpdatedEventArgs args)
        {
            if (args.EventAfter == null) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var de = args.EventAfter;

                if (de.Status == DiscordScheduledGuildEventStatus.Cancelled)
                {
                    var toRemove = await db.CalendarEvents
                        .FirstOrDefaultAsync(e => e.DiscordEventId == de.Id);
                    if (toRemove != null)
                    {
                        db.CalendarEvents.Remove(toRemove);
                        await db.SaveChangesAsync();
                    }
                    return;
                }

                if (de.Status == DiscordScheduledGuildEventStatus.Completed)
                    return;

                var existing = await db.CalendarEvents
                    .FirstOrDefaultAsync(e => e.DiscordEventId == de.Id);

                if (existing == null)
                {
                    db.CalendarEvents.Add(new Models.CalendarEvent
                    {
                        Title = de.Name ?? "(без названия)",
                        Description = string.IsNullOrEmpty(de.Description) ? null : de.Description,
                        Location = de.ChannelId?.ToString(),
                        StartUtc = DateTime.SpecifyKind(de.StartTime.UtcDateTime, DateTimeKind.Utc),
                        EndUtc = DateTime.SpecifyKind(
                            (de.EndTime?.UtcDateTime ?? de.StartTime.UtcDateTime.AddHours(2)),
                            DateTimeKind.Utc),
                        DiscordEventId = de.Id,
                        Source = "Discord",
                        LastSyncedUtc = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                    return;
                }

                existing.Title = de.Name ?? existing.Title;
                existing.Description = string.IsNullOrEmpty(de.Description) ? null : de.Description;
                existing.StartUtc = DateTime.SpecifyKind(de.StartTime.UtcDateTime, DateTimeKind.Utc);
                if (de.EndTime.HasValue)
                    existing.EndUtc = DateTime.SpecifyKind(de.EndTime.Value.UtcDateTime, DateTimeKind.Utc);
                existing.Location = de.ChannelId?.ToString() ?? existing.Location;
                existing.Source = "Discord";
                existing.LastSyncedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ScheduledEventSyncHandler failed for event {Id}", args.EventAfter.Id);
            }
        }
    }
}