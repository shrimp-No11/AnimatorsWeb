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

        public async Task<IActionResult> Index()
        {
            var participants = await _db.Participants.ToListAsync();
            var winners = await _db.Winners.ToListAsync();
            var jams = await _db.AnimJams
                .Select(j => new { j.Number, j.StartDate })
                .ToDictionaryAsync(j => j.Number, j => j.StartDate);

            var allUserIds = participants.Select(p => p.DiscordUserId)
                .Concat(winners.Select(w => w.DiscordUserId))
                .Distinct()
                .ToList();

            var rows = new List<ParticipantRow>();

            foreach (var userId in allUserIds)
            {
                var userParticipations = participants.Where(p => p.DiscordUserId == userId).ToList();
                var userWins = winners.Where(w => w.DiscordUserId == userId).ToList();

                // Имя — из последнего участия (свежий снимок ника)
                var lastParticipation = userParticipations
                    .OrderByDescending(p => p.AnimJamNumber)
                    .FirstOrDefault();

                string displayName = lastParticipation?.DisplayName ?? $"ID {userId}";

                // Победы по аниджемам
                var winsByJam = userWins.GroupBy(w => w.AnimJamNumber).ToList();

                // Абсолютные — там, где и Idea, и Technical
                var absoluteJamNumbers = winsByJam
                    .Where(g => g.Any(w => w.Nomination == NominationType.Idea) &&
                                g.Any(w => w.Nomination == NominationType.Technical))
                    .Select(g => g.Key)
                    .ToHashSet();

                // Аниджемы, где есть хоть одна победа (кроме HonorableMention)
                int winsCount = winsByJam.Count(g => g.Any(w =>
                    w.Nomination == NominationType.Idea ||
                    w.Nomination == NominationType.Technical ||
                    w.Nomination == NominationType.Winner));

                // Разбивка по номинациям без абсолютных
                int ideaWins = userWins.Count(w =>
                    w.Nomination == NominationType.Idea &&
                    !absoluteJamNumbers.Contains(w.AnimJamNumber));

                int techWins = userWins.Count(w =>
                    w.Nomination == NominationType.Technical &&
                    !absoluteJamNumbers.Contains(w.AnimJamNumber));

                int legacyWins = userWins.Count(w => w.Nomination == NominationType.Winner);
                int honorable = userWins.Count(w => w.Nomination == NominationType.HonorableMention);

                // Первое и последнее появление
                var allJamNumbers = userParticipations.Select(p => p.AnimJamNumber)
                    .Concat(userWins.Select(w => w.AnimJamNumber))
                    .Distinct()
                    .ToList();

                int firstJam = allJamNumbers.Any() ? allJamNumbers.Min() : 0;
                int lastJam = allJamNumbers.Any() ? allJamNumbers.Max() : 0;

                rows.Add(new ParticipantRow
                {
                    DiscordUserId = userId,
                    DisplayName = displayName,
                    ParticipationsCount = userParticipations.Select(p => p.AnimJamNumber).Distinct().Count(),
                    WorksCount = userParticipations.Count,
                    WinsCount = winsCount,
                    AbsoluteVictories = absoluteJamNumbers.Count,
                    IdeaWins = ideaWins,
                    TechnicalWins = techWins,
                    LegacyWins = legacyWins,
                    HonorableMentions = honorable,
                    FirstAnimJam = firstJam,
                    LastAnimJam = lastJam,
                    FirstDate = jams.TryGetValue(firstJam, out var fd) ? fd : DateTime.MinValue,
                    LastDate = jams.TryGetValue(lastJam, out var ld) ? ld : DateTime.MinValue
                });
            }

            var model = new ParticipantsListViewModel { Rows = rows };
            return View(model);
        }
    }
}