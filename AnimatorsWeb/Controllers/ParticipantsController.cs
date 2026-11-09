using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Controllers
{
    [AllowAnonymous]
    public class ParticipantsController : Controller
    {
        private readonly AppDbContext _db;

        public ParticipantsController(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string? tab)
        {
            tab = (tab ?? "all").ToLowerInvariant();
            if (tab != "anijam" && tab != "artjam") tab = "anijam";

            EventType? filter = tab switch
            {
                "anijam" => EventType.AniJam,
                "artjam" => EventType.ArtJam,
                _ => null
            };

            var allParticipants = await _db.Participants.ToListAsync();
            var allWinners = await _db.Winners.ToListAsync();

            var jams = await _db.AnimJams
                .Select(j => new { j.Number, j.StartDate, j.EventType })
                .ToDictionaryAsync(j => (j.EventType, j.Number), j => j.StartDate);

            var participants = filter.HasValue
                ? allParticipants.Where(p => p.EventType == filter.Value).ToList()
                : allParticipants;

            var winners = filter.HasValue
                ? allWinners.Where(w => w.EventType == filter.Value).ToList()
                : allWinners;

            var allUserIds = participants.Select(p => p.DiscordUserId)
                .Concat(winners.Select(w => w.DiscordUserId))
                .Distinct()
                .ToList();

            var rows = new List<ParticipantRow>();

            foreach (var userId in allUserIds)
            {
                var userParticipations = participants.Where(p => p.DiscordUserId == userId).ToList();
                var userWins = winners.Where(w => w.DiscordUserId == userId).ToList();

                var lastParticipation = userParticipations
                    .Where(p => jams.ContainsKey((p.EventType, p.AnimJamNumber)))
                    .OrderByDescending(p => jams[(p.EventType, p.AnimJamNumber)])
                    .FirstOrDefault()
                    ?? userParticipations.LastOrDefault();

                string displayName = !string.IsNullOrEmpty(lastParticipation?.DisplayName)
                    ? lastParticipation.DisplayName
                    : "[аккаунт удалён]";

                var winsByJam = userWins.GroupBy(w => (w.EventType, w.AnimJamNumber)).ToList();

                var absoluteJamNumbers = winsByJam
                    .Where(g => g.Any(w => w.Nomination == NominationType.Idea) &&
                                g.Any(w => w.Nomination == NominationType.Technical))
                    .Select(g => g.Key)
                    .ToHashSet();

                int winsCount = winsByJam.Count(g => g.Any(w =>
                    w.Nomination == NominationType.Idea ||
                    w.Nomination == NominationType.Technical ||
                    w.Nomination == NominationType.Winner));

                int ideaWins = userWins.Count(w =>
                    w.Nomination == NominationType.Idea &&
                    !absoluteJamNumbers.Contains((w.EventType, w.AnimJamNumber)));

                int techWins = userWins.Count(w =>
                    w.Nomination == NominationType.Technical &&
                    !absoluteJamNumbers.Contains((w.EventType, w.AnimJamNumber)));

                int legacyWins = userWins.Count(w => w.Nomination == NominationType.Winner);
                int honorable = userWins.Count(w => w.Nomination == NominationType.HonorableMention);

                var allEvents = userParticipations.Select(p => (p.EventType, p.AnimJamNumber))
                    .Concat(userWins.Select(w => (w.EventType, w.AnimJamNumber)))
                    .Distinct()
                    .Where(e => jams.ContainsKey(e))
                    .OrderBy(e => jams[e])
                    .ToList();

                var firstPair = allEvents.FirstOrDefault();
                var lastPair = allEvents.LastOrDefault();

                rows.Add(new ParticipantRow
                {
                    DiscordUserId = userId,
                    DisplayName = displayName,
                    ParticipationsCount = userParticipations
                        .Select(p => (p.EventType, p.AnimJamNumber)).Distinct().Count(),
                    WorksCount = userParticipations.Count,
                    WinsCount = winsCount,
                    AbsoluteVictories = absoluteJamNumbers.Count,
                    IdeaWins = ideaWins,
                    TechnicalWins = techWins,
                    LegacyWins = legacyWins,
                    HonorableMentions = honorable,
                    FirstAnimJam = firstPair.Item2,
                    LastAnimJam = lastPair.Item2,
                    FirstEventType = firstPair.Item1,
                    LastEventType = lastPair.Item1,
                    FirstDate = firstPair.Item2 > 0 ? jams[firstPair] : DateTime.MinValue,
                    LastDate = lastPair.Item2 > 0 ? jams[lastPair] : DateTime.MinValue
                });
            }

            var model = new ParticipantsListViewModel
            {
                Rows = rows,
                Tab = tab,
                TotalCount = allParticipants.Select(p => p.DiscordUserId).Distinct().Count(),
                AniJamCount = allParticipants
                    .Where(p => p.EventType == EventType.AniJam)
                    .Select(p => p.DiscordUserId).Distinct().Count(),
                ArtJamCount = allParticipants
                    .Where(p => p.EventType == EventType.ArtJam)
                    .Select(p => p.DiscordUserId).Distinct().Count()
            };
            return View(model);
        }
    }
}