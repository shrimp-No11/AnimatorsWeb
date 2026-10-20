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

            // === Подтверждённые связи (только с известным LinkedDiscordId) ===
            var links = await _db.LinkedAccounts
                .Where(la => la.IsConfirmed && la.LinkedDiscordId != null)
                .Select(la => new { la.PrimaryDiscordId, Linked = la.LinkedDiscordId!.Value })
                .ToListAsync();

            // Двунаправленный граф связей
            var adjacency = new Dictionary<ulong, HashSet<ulong>>();
            void AddEdge(ulong a, ulong b)
            {
                if (!adjacency.TryGetValue(a, out var sa)) adjacency[a] = sa = new HashSet<ulong>();
                if (!adjacency.TryGetValue(b, out var sb)) adjacency[b] = sb = new HashSet<ulong>();
                sa.Add(b);
                sb.Add(a);
            }
            foreach (var l in links) AddEdge(l.PrimaryDiscordId, l.Linked);

            // Все id, у которых есть хоть какие-то данные
            var allUserIds = participants.Select(p => p.DiscordUserId)
                .Concat(winners.Select(w => w.DiscordUserId))
                .Distinct()
                .ToList();

            // Обход в ширину — компоненты связности
            var clusters = new List<List<ulong>>();
            var visited = new HashSet<ulong>();

            foreach (var userId in allUserIds)
            {
                if (visited.Contains(userId)) continue;

                var cluster = new List<ulong>();
                var queue = new Queue<ulong>();
                queue.Enqueue(userId);
                visited.Add(userId);

                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    cluster.Add(cur);

                    if (adjacency.TryGetValue(cur, out var neighbors))
                        foreach (var n in neighbors)
                            if (visited.Add(n)) queue.Enqueue(n);
                }

                clusters.Add(cluster);
            }

            var rows = new List<ParticipantRow>();

            foreach (var cluster in clusters)
            {
                var idSet = cluster.ToHashSet();

                // --- представитель: у кого самая свежая запись (Participants, потом Winners) ---
                ulong repId = cluster[0];
                int bestJam = -1;

                foreach (var id in cluster)
                {
                    int lastP = participants.Where(p => p.DiscordUserId == id)
                        .Select(p => p.AnimJamNumber).DefaultIfEmpty(0).Max();
                    int lastW = winners.Where(w => w.DiscordUserId == id)
                        .Select(w => w.AnimJamNumber).DefaultIfEmpty(0).Max();
                    int last = Math.Max(lastP, lastW);

                    if (last > bestJam)
                    {
                        bestJam = last;
                        repId = id;
                    }
                }

                // --- статистика по ВСЕМ id кластера ---
                var clusterParticipations = participants.Where(p => idSet.Contains(p.DiscordUserId)).ToList();
                var clusterWins = winners.Where(w => idSet.Contains(w.DiscordUserId)).ToList();

                // Имя — из самой свежей записи в кластере
                var lastParticipation = clusterParticipations
                    .OrderByDescending(p => p.AnimJamNumber)
                    .FirstOrDefault();
                string displayName = lastParticipation?.DisplayName ?? $"ID {repId}";

                var winsByJam = clusterWins.GroupBy(w => w.AnimJamNumber).ToList();

                var absoluteJamNumbers = winsByJam
                    .Where(g => g.Any(w => w.Nomination == NominationType.Idea) &&
                                g.Any(w => w.Nomination == NominationType.Technical))
                    .Select(g => g.Key)
                    .ToHashSet();

                int winsCount = winsByJam.Count(g => g.Any(w =>
                    w.Nomination == NominationType.Idea ||
                    w.Nomination == NominationType.Technical ||
                    w.Nomination == NominationType.Winner));

                int ideaWins = clusterWins.Count(w =>
                    w.Nomination == NominationType.Idea &&
                    !absoluteJamNumbers.Contains(w.AnimJamNumber));

                int techWins = clusterWins.Count(w =>
                    w.Nomination == NominationType.Technical &&
                    !absoluteJamNumbers.Contains(w.AnimJamNumber));

                int legacyWins = clusterWins.Count(w => w.Nomination == NominationType.Winner);
                int honorable = clusterWins.Count(w => w.Nomination == NominationType.HonorableMention);

                var allJamNumbers = clusterParticipations.Select(p => p.AnimJamNumber)
                    .Concat(clusterWins.Select(w => w.AnimJamNumber))
                    .Distinct()
                    .ToList();

                int firstJam = allJamNumbers.Any() ? allJamNumbers.Min() : 0;
                int lastJam = allJamNumbers.Any() ? allJamNumbers.Max() : 0;

                rows.Add(new ParticipantRow
                {
                    DiscordUserId = repId,
                    DisplayName = displayName,
                    ParticipationsCount = clusterParticipations.Select(p => p.AnimJamNumber).Distinct().Count(),
                    WorksCount = clusterParticipations.Count,
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