using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
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
            EventType eventType;
            if (string.Equals(type, "AniJam", StringComparison.OrdinalIgnoreCase))
                eventType = EventType.AniJam;
            else if (string.Equals(type, "ArtJam", StringComparison.OrdinalIgnoreCase))
                eventType = EventType.ArtJam;
            else
                return NotFound();

            var jam = await _db.AnimJams
                .FirstOrDefaultAsync(j => j.Number == number && j.EventType == eventType);
            if (jam == null) return NotFound();

            var participants = await _db.Participants
                .Where(p => p.AnimJamNumber == number && p.EventType == eventType)
                .OrderBy(p => p.DisplayName)
                .ToListAsync();

            var winners = await _db.Winners
                .Where(w => w.AnimJamNumber == number && w.EventType == eventType)
                .ToListAsync();

            var model = new EventDetailsViewModel
            {
                Number = jam.Number,
                EventType = eventType,
                Theme = jam.Theme,
                StartDate = jam.StartDate,
                EndDate = jam.EndDate,
                AnnouncementUrl = jam.AnounceMessage,
                ResultsUrl = jam.ResultsMessageUrl,
                ParticipantsCount = participants.Select(p => p.DiscordUserId).Distinct().Count(),
                WinnersCount = winners
                    .Where(w => w.Nomination != NominationType.HonorableMention)
                    .Select(w => w.DiscordUserId)
                    .Distinct()
                    .Count()
            };

            if (eventType == EventType.AniJam)
                BuildAniJamWinners(model, participants, winners);
            else
                BuildArtJamWinners(model, participants, winners);

            BuildWorks(model, participants, winners, eventType);

            return View(model);
        }

        private void BuildAniJamWinners(
            EventDetailsViewModel model,
            List<Participant> participants,
            List<Winner> winners)
        {
            var winsByWork = winners.GroupBy(w => NormalizeUrl(w.WorkUrl)).ToList();

            var workNominations = winsByWork.ToDictionary(
                g => g.Key,
                g => g.Select(w => w.Nomination).Distinct().ToHashSet());

            var absoluteWorks = workNominations
                .Where(kv => kv.Value.Contains(NominationType.Idea) &&
                             kv.Value.Contains(NominationType.Technical))
                .Select(kv => kv.Key)
                .ToHashSet();

            // Абсолютные
            foreach (var workUrl in absoluteWorks)
            {
                var workWinners = winsByWork.First(g => g.Key == workUrl);
                var userIds = workWinners.Select(w => w.DiscordUserId).Distinct().ToList();

                var group = new EventWinnerGroup
                {
                    NominationDisplayHtml = "🏅 Абсолютный победитель"
                };

                foreach (var userId in userIds)
                {
                    var pInfo = participants.FirstOrDefault(p => p.DiscordUserId == userId);
                    group.Winners.Add(new EventWinnerPerson
                    {
                        DiscordUserId = userId,
                        DisplayName = pInfo?.DisplayName ?? $"ID {userId}",
                        WorkUrl = workUrl,
                        MessageUrl = pInfo?.MessageUrl ?? ""
                    });
                }

                model.WinnerGroups.Add(group);
            }

            int NominationOrder(NominationType n) => n switch
            {
                NominationType.Idea => 0,
                NominationType.Technical => 1,
                NominationType.Winner => 2,
                NominationType.HonorableMention => 3,
                _ => 4
            };

            foreach (var workGroup in winsByWork
                .Where(g => !absoluteWorks.Contains(g.Key))
                .OrderBy(g => NominationOrder(g.First().Nomination)))
            {
                var workUrl = workGroup.Key;
                var nomination = workGroup.First().Nomination;
                var userIds = workGroup.Select(w => w.DiscordUserId).Distinct().ToList();

                var group = new EventWinnerGroup
                {
                    NominationDisplayHtml = NominationHelper.GetDisplayHtml(nomination, model.Number)
                };

                foreach (var userId in userIds)
                {
                    var pInfo = participants.FirstOrDefault(p => p.DiscordUserId == userId);
                    group.Winners.Add(new EventWinnerPerson
                    {
                        DiscordUserId = userId,
                        DisplayName = pInfo?.DisplayName ?? $"ID {userId}",
                        WorkUrl = workUrl,
                        MessageUrl = pInfo?.MessageUrl ?? ""
                    });
                }

                model.WinnerGroups.Add(group);
            }
        }

        private void BuildArtJamWinners(
            EventDetailsViewModel model,
            List<Participant> participants,
            List<Winner> winners)
        {
            var winnerWorkUrls = winners
                .Where(w => w.Nomination == NominationType.Winner)
                .Select(w => w.WorkUrl)
                .ToHashSet();

            if (winnerWorkUrls.Count == 0) return;

            var group = new EventWinnerGroup
            {
                NominationDisplayHtml = "🏆 Победители"
            };

            var seen = new HashSet<ulong>();

            foreach (var workUrl in winnerWorkUrls)
            {
                var workParticipants = participants
                    .Where(p => p.WorkUrl == workUrl)
                    .OrderBy(p => p.DisplayName)
                    .ToList();

                foreach (var p in workParticipants)
                {
                    if (!seen.Add(p.DiscordUserId)) continue;
                    group.Winners.Add(new EventWinnerPerson
                    {
                        DiscordUserId = p.DiscordUserId,
                        DisplayName = p.DisplayName,
                        WorkUrl = p.WorkUrl,
                        MessageUrl = p.MessageUrl
                    });
                }
            }

            if (group.Winners.Count > 0)
                model.WinnerGroups.Add(group);
        }

        private void BuildWorks(
            EventDetailsViewModel model,
            List<Participant> participants,
            List<Winner> winners,
            EventType eventType)
        {
            var winsByWork = winners.GroupBy(w => NormalizeUrl(w.WorkUrl)).ToList();
            var workNominations = winsByWork.ToDictionary(
                g => g.Key,
                g => g.Select(w => w.Nomination).Distinct().ToHashSet());

            var absoluteWorks = workNominations
                .Where(kv => kv.Value.Contains(NominationType.Idea) &&
                             kv.Value.Contains(NominationType.Technical))
                .Select(kv => kv.Key)
                .ToHashSet();

            int PriorityForWork(string workUrl)
            {
                if (eventType == EventType.ArtJam)
                {
                    return workNominations.TryGetValue(workUrl, out var noms) &&
                           noms.Contains(NominationType.Winner) ? 0 : 100;
                }

                if (absoluteWorks.Contains(workUrl)) return 0;
                if (!workNominations.TryGetValue(workUrl, out var n2) || n2.Count == 0) return 100;
                if (n2.Contains(NominationType.Idea)) return 1;
                if (n2.Contains(NominationType.Technical)) return 2;
                if (n2.Contains(NominationType.Winner)) return 3;
                if (n2.Contains(NominationType.HonorableMention)) return 4;
                return 5;
            }

            string BadgeForWork(string workUrl)
            {
                if (eventType == EventType.ArtJam)
                {
                    return workNominations.TryGetValue(workUrl, out var noms) &&
                           noms.Contains(NominationType.Winner) ? EmojiHelper.Trophy : "";
                }

                if (absoluteWorks.Contains(workUrl)) return EmojiHelper.Trophy;
                if (!workNominations.TryGetValue(workUrl, out var n2) || n2.Count == 0) return "";
                if (n2.Contains(NominationType.Winner)) return EmojiHelper.Trophy;
                if (n2.Contains(NominationType.Idea)) return EmojiHelper.Star;
                if (n2.Contains(NominationType.Technical)) return EmojiHelper.Hammer;
                if (n2.Contains(NominationType.HonorableMention)) return EmojiHelper.GoldPoopHtml;
                return "";
            }

            var groupedWorks = participants
                .GroupBy(p => NormalizeUrl(p.WorkUrl))
                .Select(g =>
                {
                    var primary = g.First();
                    var normalizedUrl = NormalizeUrl(primary.WorkUrl);

                    var people = g
                        .Select(p => new EventPerson
                        {
                            DiscordUserId = p.DiscordUserId,
                            DisplayName = p.DisplayName
                        })
                        .GroupBy(x => x.DiscordUserId)
                        .Select(x => x.First())
                        .ToList();

                    return new
                    {
                        Primary = primary,
                        People = people,
                        Priority = PriorityForWork(normalizedUrl),
                        Badge = BadgeForWork(normalizedUrl),
                        IsWinner = !string.IsNullOrEmpty(BadgeForWork(normalizedUrl))
                    };
                })
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.People.FirstOrDefault()?.DisplayName ?? "")
                .ToList();

            foreach (var group in groupedWorks)
            {
                var p = group.Primary;

                var item = new EventWorkCard
                {
                    Id = p.Id,
                    People = group.People,
                    WorkUrl = p.WorkUrl,
                    MessageUrl = p.MessageUrl,
                    IsWinner = group.IsWinner,
                    BadgeHtml = group.Badge,
                    AttachmentsCount = p.AttachmentCount
                };

                var ytId = YouTubeHelper.ExtractId(p.WorkUrl);
                if (ytId != null)
                {
                    item.MediaType = "youtube";
                    item.ThumbnailUrl = $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg";
                }
                else if (p.WorkUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                         p.WorkUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
                         p.WorkUrl.Contains(".mov", StringComparison.OrdinalIgnoreCase))
                {
                    item.MediaType = "video";
                    item.ThumbnailUrl = $"/Featured/Thumbnail/{p.Id}";
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(p.WorkUrl,
                    @"\.(gif|png|jpg|jpeg|webp|avif)(\?|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    item.MediaType = "image";
                    item.ThumbnailUrl = $"/Featured/Thumbnail/{p.Id}";
                }
                else
                {
                    item.MediaType = "link";
                }

                model.Works.Add(item);
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.TrimEnd('&', '?', ' ');
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