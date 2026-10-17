using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AnimatorsWeb.Controllers
{
    public class AnimJamsController : Controller
    {
        private readonly AppDbContext _db;

        public AnimJamsController(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            var jams = await _db.AnimJams
                .OrderByDescending(j => j.Number)
                .ToListAsync();

            // Счётчики участников и победителей
            var participantCounts = await _db.Participants
                .GroupBy(p => p.AnimJamNumber)
                .Select(g => new {
                    JamNumber = g.Key,
                    Count = g.Select(p => p.DiscordUserId).Distinct().Count()
                })
                .ToDictionaryAsync(x => x.JamNumber, x => x.Count);

            var winnerCounts = await _db.Winners
                .Where(w => w.Nomination != NominationType.HonorableMention)
                .GroupBy(w => w.AnimJamNumber)
                .Select(g => new {
                    JamNumber = g.Key,
                    Count = g.Select(w => w.DiscordUserId).Distinct().Count()
                })
                .ToDictionaryAsync(x => x.JamNumber, x => x.Count);

            // Топ-2 победителя для каждой карточки
            var allWinners = await _db.Winners
                .OrderBy(w => w.AnimJamNumber)
                .ThenBy(w => w.Nomination)
                .ToListAsync();

            var winnersByJam = allWinners
                .GroupBy(w => w.AnimJamNumber)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .GroupBy(w => w.DiscordUserId)
                        .Select(ug => ug.First())
                        .Take(2)
                        .ToList());

            // Имена участников для подстановки ников победителей
            var participantNames = await _db.Participants
                .Select(p => new { p.AnimJamNumber, p.DiscordUserId, p.DisplayName })
                .ToListAsync();

            var namesLookup = participantNames
                .GroupBy(p => p.AnimJamNumber)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .GroupBy(x => x.DiscordUserId)
                        .ToDictionary(x => x.Key, x => x.First().DisplayName));

            var model = new AnimJamListViewModel { TotalCount = jams.Count };

            foreach (var jam in jams)
            {
                var card = new AnimJamCard
                {
                    Number = jam.Number,
                    Theme = jam.Theme,
                    StartDate = jam.StartDate,
                    EndDate = jam.EndDate,
                    ParticipantsCount = participantCounts.GetValueOrDefault(jam.Number, 0),
                    WinnersCount = winnerCounts.GetValueOrDefault(jam.Number, 0),
                    AnnouncementUrl = jam.AnounceMessage ?? "",
                    ResultsUrl = jam.ResultsMessageUrl ?? ""
                };

                if (winnersByJam.TryGetValue(jam.Number, out var jamWinners))
                {
                    var names = namesLookup.GetValueOrDefault(jam.Number);
                    foreach (var w in jamWinners)
                    {
                        if (names != null && names.TryGetValue(w.DiscordUserId, out var name))
                            card.TopWinners.Add(name);
                    }
                }

                int year = jam.StartDate.Year;

                var group = model.Years.FirstOrDefault(y => y.Year == year);
                if (group == null)
                {
                    group = new AnimJamYearGroup { Year = year };
                    model.Years.Add(group);
                }
                group.Jams.Add(card);
            }

            model.Years = model.Years.OrderByDescending(y => y.Year).ToList();
            foreach (var y in model.Years)
                y.Jams = y.Jams.OrderByDescending(j => j.Number).ToList();

            return View(model);
        }
        public async Task<IActionResult> Details(int number)
        {
            var jam = await _db.AnimJams.FirstOrDefaultAsync(j => j.Number == number);
            if (jam == null) return NotFound();

            var participants = await _db.Participants
                .Where(p => p.AnimJamNumber == number)
                .OrderBy(p => p.DisplayName)
                .ToListAsync();

            var winners = await _db.Winners
                .Where(w => w.AnimJamNumber == number)
                .ToListAsync();

            var model = new AnimJamDetailsViewModel
            {
                Number = jam.Number,
                Theme = jam.Theme,
                StartDate = jam.StartDate,
                EndDate = jam.EndDate,
                AnnouncementUrl = jam.AnounceMessage ?? "",
                ResultsUrl = jam.ResultsMessageUrl ?? "",
                ParticipantsCount = participants.Select(p => p.DiscordUserId).Distinct().Count(),
                WinnersCount = winners
                    .Where(w => w.Nomination != NominationType.HonorableMention)
                    .Select(w => w.DiscordUserId)
                    .Distinct()
                    .Count()
            };

            // === Группируем Winners по работе (с нормализацией URL) ===
            var winsByWork = winners
                .GroupBy(w => NormalizeUrl(w.WorkUrl))
                .ToList();

            var workNominations = winsByWork.ToDictionary(
                g => g.Key,
                g => g.Select(w => w.Nomination).Distinct().ToHashSet());

            var absoluteWorks = workNominations
                .Where(kv => kv.Value.Contains(NominationType.Idea) &&
                             kv.Value.Contains(NominationType.Technical))
                .Select(kv => kv.Key)
                .ToHashSet();

            // === Формируем группы победителей ===
            // Сначала — абсолютные
            foreach (var workUrl in absoluteWorks)
            {
                var workWinners = winsByWork.First(g => g.Key == workUrl);
                var userIds = workWinners.Select(w => w.DiscordUserId).Distinct().ToList();

                var group = new WinnerGroup
                {
                    NominationDisplay = "🏅 Абсолютный победитель",
                    NominationDisplayHtml = "🏅 Абсолютный победитель"
                };

                foreach (var userId in userIds)
                {
                    var pInfo = participants.FirstOrDefault(p => p.DiscordUserId == userId);
                    group.Winners.Add(new WinnerWork
                    {
                        DiscordUserId = userId,
                        DisplayName = pInfo?.DisplayName ?? $"ID {userId}",
                        WorkUrl = workUrl,
                        MessageUrl = pInfo?.MessageUrl ?? ""
                    });
                }

                model.Winners.Add(group);
            }

            // Потом — остальные, по номинациям
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

                var group = new WinnerGroup
                {
                    NominationDisplay = NominationHelper.GetDisplay(nomination, jam.Number),
                    NominationDisplayHtml = NominationHelper.GetDisplayHtml(nomination, jam.Number)
                };

                foreach (var userId in userIds)
                {
                    var pInfo = participants.FirstOrDefault(p => p.DiscordUserId == userId);
                    group.Winners.Add(new WinnerWork
                    {
                        DiscordUserId = userId,
                        DisplayName = pInfo?.DisplayName ?? $"ID {userId}",
                        WorkUrl = workUrl,
                        MessageUrl = pInfo?.MessageUrl ?? ""
                    });
                }

                model.Winners.Add(group);
            }

            // === Значки для работ: определяем приоритет по конкретной работе ===
            // Для каждой работы — набор номинаций
            int PriorityForWork(string workUrl)
            {
                if (absoluteWorks.Contains(workUrl)) return 0;

                if (!workNominations.TryGetValue(workUrl, out var noms) || noms.Count == 0)
                    return 100;

                if (noms.Contains(NominationType.Idea)) return 1;
                if (noms.Contains(NominationType.Technical)) return 2;
                if (noms.Contains(NominationType.Winner)) return 3;
                if (noms.Contains(NominationType.HonorableMention)) return 4;
                return 5;
            }

            // Значок для конкретной работы
            string? BadgeForWork(string workUrl)
            {
                if (absoluteWorks.Contains(workUrl)) return EmojiHelper.Trophy;

                if (!workNominations.TryGetValue(workUrl, out var noms) || noms.Count == 0)
                    return null;

                if (noms.Contains(NominationType.Winner)) return EmojiHelper.Trophy;
                if (noms.Contains(NominationType.Idea)) return EmojiHelper.Star;
                if (noms.Contains(NominationType.Technical)) return EmojiHelper.Hammer;
                if (noms.Contains(NominationType.HonorableMention)) return EmojiHelper.GoldPoopHtml;

                return null;
            }

            // === Список работ ===
            var groupedWorks = participants
                .GroupBy(p => NormalizeUrl(p.WorkUrl))
                .Select(g =>
                {
                    var primary = g.First();
                    var normalizedUrl = NormalizeUrl(primary.WorkUrl);

                    var people = g
                        .Select(p => new ParticipantPerson
                        {
                            DiscordUserId = p.DiscordUserId,
                            DisplayName = p.DisplayName
                        })
                        .GroupBy(x => x.DiscordUserId)
                        .Select(x => x.First())
                        .ToList();

                    int priority = PriorityForWork(normalizedUrl);
                    string? badge = BadgeForWork(normalizedUrl);

                    return new
                    {
                        Primary = primary,
                        People = people,
                        Priority = priority,
                        Badge = badge,
                        IsWinner = badge != null
                    };
                })
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.People.First().DisplayName)
                .ToList();

            foreach (var group in groupedWorks)
            {
                var p = group.Primary;

                var item = new ParticipantWork
                {
                    Id = p.Id,
                    People = group.People,
                    WorkUrl = p.WorkUrl,
                    MessageUrl = p.MessageUrl,
                    IsWinner = group.IsWinner,
                    BadgeHtml = group.Badge ?? ""
                };

                var ytId = GetYouTubeId(p.WorkUrl);
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
                    item.ThumbnailUrl = $"/Media/StreamWork/{p.Id}";
                }
                else if (Regex.IsMatch(p.WorkUrl, @"\.(gif|png|jpg|jpeg)(\?|$)", RegexOptions.IgnoreCase))
                {
                    item.MediaType = "image";
                    item.ThumbnailUrl = p.WorkUrl;
                }
                else
                {
                    item.MediaType = "link";
                }

                model.Participants.Add(item);
            }

            return View(model);
        }
        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.TrimEnd('&', '?', ' ');
        }

        private static string? GetYouTubeId(string url)
        {
            var regex = new Regex(@"(?:youtu\.be/|youtube\.com/(?:watch\?v=|embed/|v/))([a-zA-Z0-9_-]{11})");
            var match = regex.Match(url);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}