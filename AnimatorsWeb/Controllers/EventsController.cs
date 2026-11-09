using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Controllers
{
    [AllowAnonymous]
    public class EventsController : Controller
    {
        private readonly AppDbContext _db;

        public EventsController(AppDbContext db) => _db = db;

        public async Task<IActionResult> Index(string? tab)
        {
            tab = (tab ?? "all").ToLowerInvariant();
            if (tab != "anijam" && tab != "artjam") tab = "all";

            var model = new EventsListViewModel { Tab = tab };

            var allEvents = await _db.AnimJams
                .OrderByDescending(j => j.Number)
                .ToListAsync();

            model.TotalCount = allEvents.Count;
            model.AniJamCount = allEvents.Count(j => j.EventType == EventType.AniJam);
            model.ArtJamCount = allEvents.Count(j => j.EventType == EventType.ArtJam);

            IEnumerable<AnimJam> filtered = tab switch
            {
                "anijam" => allEvents.Where(j => j.EventType == EventType.AniJam),
                "artjam" => allEvents.Where(j => j.EventType == EventType.ArtJam),
                _ => allEvents
            };

            // Счётчики
            var participantCounts = await _db.Participants
                .GroupBy(p => new { p.EventType, p.AnimJamNumber })
                .Select(g => new
                {
                    g.Key.EventType,
                    g.Key.AnimJamNumber,
                    Count = g.Select(p => p.DiscordUserId).Distinct().Count()
                })
                .ToListAsync();

            var pDict = participantCounts.ToDictionary(
                x => (x.EventType, x.AnimJamNumber), x => x.Count);

            var winnerCounts = await _db.Winners
                .Where(w => w.Nomination != NominationType.HonorableMention)
                .GroupBy(w => new { w.EventType, w.AnimJamNumber })
                .Select(g => new
                {
                    g.Key.EventType,
                    g.Key.AnimJamNumber,
                    Count = g.Select(w => w.DiscordUserId).Distinct().Count()
                })
                .ToListAsync();

            var wDict = winnerCounts.ToDictionary(
                x => (x.EventType, x.AnimJamNumber), x => x.Count);

            // Топ-2 победителя
            var allWinners = await _db.Winners
                .Where(w => w.Nomination != NominationType.HonorableMention)
                .OrderBy(w => w.EventType)
                .ThenBy(w => w.AnimJamNumber)
                .ThenBy(w => w.Nomination)
                .ToListAsync();

            var topByEvent = allWinners
                .GroupBy(w => (w.EventType, w.AnimJamNumber))
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(w => w.DiscordUserId)
                          .Select(ug => ug.First())
                          .Take(2)
                          .ToList());

            // Имена
            var participantNames = await _db.Participants
                .Select(p => new { p.EventType, p.AnimJamNumber, p.DiscordUserId, p.DisplayName })
                .ToListAsync();

            var namesLookup = participantNames
                .GroupBy(p => (p.EventType, p.AnimJamNumber))
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.DiscordUserId)
                          .ToDictionary(x => x.Key, x => x.First().DisplayName));


            // ===== Статус последнего/текущего АниДжема =====
            // Для вкладки «АртДжем» не показываем (это AniJam-логика)
            if (tab != "artjam")
            {
                var lastAniJam = allEvents
                    .Where(j => j.EventType == EventType.AniJam)
                    .OrderByDescending(j => j.Number)
                    .FirstOrDefault();

                if (lastAniJam != null)
                {
                    var nextNumber = lastAniJam.Number + 1;

                    var themePoll = await _db.ThemePolls
                        .Where(p => p.AnimJamNumber == nextNumber && p.EventType == EventType.AniJam)
                        .OrderByDescending(p => p.CreatedAt)
                        .FirstOrDefaultAsync();

                    var winnerPoll = await _db.WinnerPolls
                        .Where(p => p.AnimJamNumber == lastAniJam.Number && p.EventType == EventType.AniJam)
                        .Where(p => p.CreatedAt >= lastAniJam.StartDate)
                        .OrderByDescending(p => p.CreatedAt)
                        .FirstOrDefaultAsync();

                    var now = DateTime.UtcNow;
                    bool hasResults = !string.IsNullOrEmpty(lastAniJam.ResultsMessageUrl);
                    bool isActive = lastAniJam.StartDate <= now && now <= lastAniJam.EndDate;

                    if (isActive)
                    {
                        model.Status = new AnimJamStatusBanner
                        {
                            Kind = "active",
                            Text = $"Идёт #{lastAniJam.Number}-й аниджем — принимаются работы",
                            EndDateUtc = lastAniJam.EndDate.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        };
                    }
                    else if (winnerPoll != null && !hasResults)
                    {
                        model.Status = new AnimJamStatusBanner
                        {
                            Kind = "voting-winners",
                            Text = $"Идёт голосование за победителей #{lastAniJam.Number}-го аниджема"
                        };
                    }
                    else if (themePoll != null)
                    {
                        model.Status = new AnimJamStatusBanner
                        {
                            Kind = "voting-theme",
                            Text = $"Идёт голосование за тему #{nextNumber}-го аниджема"
                        };
                    }
                    else if (hasResults)
                    {
                        model.Status = new AnimJamStatusBanner
                        {
                            Kind = "ended",
                            Text = $"#{lastAniJam.Number}-й аниджем закончился — скоро начнётся новый"
                        };
                    }
                    else if (now > lastAniJam.EndDate)
                    {
                        model.Status = new AnimJamStatusBanner
                        {
                            Kind = "closed",
                            Text = $"#{lastAniJam.Number}-й аниджем завершён — ждём подведения итогов"
                        };
                    }
                }
            }


            // Карточки
            foreach (var jam in filtered)
            {
                var card = new EventCard
                {
                    Type = jam.EventType,
                    Number = jam.Number,
                    Theme = jam.Theme,
                    StartDate = jam.StartDate,
                    EndDate = jam.EndDate,
                    ParticipantsCount = pDict.GetValueOrDefault((jam.EventType, jam.Number), 0),
                    WinnersCount = wDict.GetValueOrDefault((jam.EventType, jam.Number), 0)
                };

                if (topByEvent.TryGetValue((jam.EventType, jam.Number), out var top))
                {
                    var names = namesLookup.GetValueOrDefault((jam.EventType, jam.Number));
                    foreach (var w in top)
                    {
                        if (names != null && names.TryGetValue(w.DiscordUserId, out var name))
                            card.TopWinners.Add(name);
                    }
                }

                int year = jam.StartDate.Year;
                var group = model.Years.FirstOrDefault(y => y.Year == year);
                if (group == null)
                {
                    group = new EventYearGroup { Year = year };
                    model.Years.Add(group);
                }
                group.Events.Add(card);
            }

            model.Years = model.Years.OrderByDescending(y => y.Year).ToList();
            foreach (var y in model.Years)
                y.Events = y.Events.OrderByDescending(e => e.StartDate).ToList();

            return View(model);
        }

        public async Task<IActionResult> Details(string type, int number)
        {
            if (string.Equals(type, "AniJam", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Details", "AnimJams", new { number });

            if (!string.Equals(type, "ArtJam", StringComparison.OrdinalIgnoreCase))
                return NotFound();

            var jam = await _db.AnimJams
                .FirstOrDefaultAsync(j => j.Number == number && j.EventType == EventType.ArtJam);
            if (jam == null) return NotFound();

            var model = new ArtJamDetailsViewModel
            {
                Number = jam.Number,
                Theme = jam.Theme,
                StartDate = jam.StartDate,
                EndDate = jam.EndDate,
                AnnouncementUrl = jam.AnounceMessage,
                ResultsUrl = jam.ResultsMessageUrl
            };

            var participants = await _db.Participants
                .Where(p => p.AnimJamNumber == number && p.EventType == EventType.ArtJam)
                .ToListAsync();

            model.ParticipantsCount = participants.Select(p => p.DiscordUserId).Distinct().Count();

            var winners = await _db.Winners
                .Where(w => w.AnimJamNumber == number
                            && w.EventType == EventType.ArtJam
                            && w.Nomination == NominationType.Winner)
                .ToListAsync();

            if (winners.Any())
            {
                var group = new ArtJamWinnerGroup { Title = "🏆 Победители" };
                var seen = new HashSet<ulong>();

                var winnerIds = winners.Select(w => w.DiscordUserId).ToHashSet();
                var winnerParticipants = participants
                    .Where(p => winnerIds.Contains(p.DiscordUserId))
                    .OrderBy(p => p.DisplayName)
                    .ToList();

                foreach (var p in winnerParticipants)
                {
                    if (!seen.Add(p.DiscordUserId)) continue;
                    group.People.Add(new ArtJamPerson
                    {
                        DiscordUserId = p.DiscordUserId,
                        DisplayName = p.DisplayName
                    });
                }

                if (group.People.Any())
                    model.WinnerGroups.Add(group);
            }

            var byWorkUrl = participants
                .Where(p => !string.IsNullOrEmpty(p.WorkUrl))
                .GroupBy(p => p.WorkUrl)
                .ToList();

            foreach (var g in byWorkUrl)
            {
                var first = g.First();
                model.Works.Add(new ArtJamWorkCard
                {
                    WorkUrl = first.WorkUrl,
                    MessageUrl = first.MessageUrl,
                    PrimaryParticipantId = first.Id,
                    MediaType = DetectMediaType(first.WorkUrl),
                    People = g.GroupBy(p => p.DiscordUserId)
                        .Select(x => new ArtJamPerson
                        {
                            DiscordUserId = x.Key,
                            DisplayName = x.First().DisplayName
                        })
                        .OrderBy(p => p.DisplayName)
                        .ToList()
                });
            }

            return View(model);
        }

        private static string DetectMediaType(string workUrl)
        {
            if (string.IsNullOrEmpty(workUrl)) return "link";
            var lower = workUrl.ToLowerInvariant();

            if (lower.Contains("youtube.com") || lower.Contains("youtu.be")) return "youtube";
            if (lower.Contains(".mp4") || lower.Contains(".webm") || lower.Contains(".mov")) return "video";
            if (System.Text.RegularExpressions.Regex.IsMatch(workUrl,
                @"\.(gif|png|jpg|jpeg|webp|avif)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "image";
            return "link";
        }
    }
}