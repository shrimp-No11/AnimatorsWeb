using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using DSharpPlus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AnimatorsWeb.Controllers
{
    [Authorize]
    public class UserController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DiscordClient _discordClient;

        public UserController(AppDbContext db, DiscordClient discordClient)
        {
            _db = db;
            _discordClient = discordClient;
        }

        public async Task<IActionResult> Profile(string? tab)
        {
            var discordIdStr = User.FindFirst("discord_id")?.Value;
            if (string.IsNullOrEmpty(discordIdStr) || !ulong.TryParse(discordIdStr, out ulong discordId))
                return RedirectToAction("Login", "Account");

            tab = (tab ?? "anijam").ToLowerInvariant();
            if (tab != "artjam") tab = "anijam";

            var model = await BuildProfileModel(discordId, User.Identity?.Name, tab);
            return View(model);
        }

        [AllowAnonymous]
        public async Task<IActionResult> Works(ulong? id = null, string? tab = null)
        {
            ulong discordId;

            if (id.HasValue)
            {
                discordId = id.Value;
            }
            else
            {
                if (!User.Identity?.IsAuthenticated ?? true)
                    return RedirectToAction("Login", "Account");

                var discordIdStr = User.FindFirst("discord_id")?.Value;
                if (!ulong.TryParse(discordIdStr, out discordId))
                    return RedirectToAction("Login", "Account");
            }

            tab = (tab ?? "anijam").ToLowerInvariant();
            if (tab != "artjam") tab = "anijam";

            var model = await BuildWorksModel(discordId, null, tab);
            return View(model);
        }

        [AllowAnonymous]

        public async Task<IActionResult> PublicProfile(ulong id, string? tab)
        {
            tab = (tab ?? "anijam").ToLowerInvariant();
            if (tab != "artjam") tab = "anijam";

            var model = await BuildProfileModel(id, null, tab);
            return View("Profile", model);
        }

        // === Общие методы ===

        private async Task<UserProfileViewModel> BuildProfileModel(ulong discordId, string? fallbackName, string tab = "anijam")
        {
            var model = new UserProfileViewModel { DiscordUserId = discordId };

            var guild = _discordClient.Guilds.Values.FirstOrDefault();
            bool resolved = false;

            if (guild != null)
            {
                try
                {
                    var member = await guild.GetMemberAsync(discordId);
                    if (member != null)
                    {
                        model.DisplayName = member.DisplayName;
                        model.Username = member.Username;
                        model.AvatarUrl = member.AvatarUrl ?? "";
                        resolved = true;
                    }
                }
                catch { }
            }

            if (!resolved)
            {
                model.DisplayName = !string.IsNullOrEmpty(fallbackName)
                    ? fallbackName
                    : await ResolveDisplayNameAsync(discordId);
            }

            // === Связанные аккаунты (все подтверждённые, достижимые по цепочке) ===
            var connectedIds = await GetConnectedIdsAsync(discordId);
            var guildForLinks = _discordClient.Guilds.Values.FirstOrDefault();
            foreach (var id in connectedIds)
            {
                string? name = null;
                if (guildForLinks != null)
                {
                    try
                    {
                        var member = await guildForLinks.GetMemberAsync(id);
                        if (member != null) name = member.DisplayName ?? member.Username;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(name))
                    name = await ResolveDisplayNameAsync(id);

                if (!string.IsNullOrEmpty(name))
                    model.LinkedAccounts.Add(new LinkedAccountDisplay { DiscordId = id, Name = name });
            }

            // Все id для статистики — связанные + сам
            var allIds = new HashSet<ulong>(connectedIds) { discordId };

            // === Участия ===
            var allParticipations = await _db.Participants
                .Where(p => allIds.Contains(p.DiscordUserId))
                .ToListAsync();

            model.AniJamParticipationsCount = allParticipations
                .Where(p => p.EventType == EventType.AniJam)
                .Select(p => p.AnimJamNumber).Distinct().Count();

            model.ArtJamParticipationsCount = allParticipations
                .Where(p => p.EventType == EventType.ArtJam)
                .Select(p => p.AnimJamNumber).Distinct().Count();

            model.Tab = tab;
            var targetType = tab == "artjam" ? EventType.ArtJam : EventType.AniJam;

            var participations = allParticipations
                .Where(p => p.EventType == targetType)
                .ToList();

            model.ParticipationsCount = participations
                .Select(p => p.AnimJamNumber)
                .Distinct()
                .Count();

            if (participations.Any())
                model.FirstParticipationDate = participations.Min(p => p.AnimJamNumber);

            // === Победы ===
            var allWins = await _db.Winners
                .Where(w => allIds.Contains(w.DiscordUserId))
                .ToListAsync();

            model.AniJamWinsCount = allWins
                .Where(w => w.EventType == EventType.AniJam && w.Nomination != NominationType.HonorableMention)
                .GroupBy(w => w.AnimJamNumber)
                .Count();

            model.ArtJamWinsCount = allWins
                .Where(w => w.EventType == EventType.ArtJam)
                .GroupBy(w => w.AnimJamNumber)
                .Count();

            var wins = allWins
                .Where(w => w.EventType == targetType)
                .ToList();

            var winsByJam = wins.GroupBy(w => w.AnimJamNumber).ToList();

            var absoluteJams = winsByJam
                .Where(g => g.Any(w => w.Nomination == NominationType.Idea) &&
                            g.Any(w => w.Nomination == NominationType.Technical))
                .ToList();

            var legacyJams = winsByJam
                .Where(g => g.Any(w => w.Nomination == NominationType.Winner))
                .ToList();

            model.AbsoluteVictoriesCount = absoluteJams.Count;
            model.LegacyVictoriesCount = legacyJams.Count;

            model.WinsCount = winsByJam.Count(g => g.Any(w =>
                w.Nomination == NominationType.Idea ||
                w.Nomination == NominationType.Technical ||
                w.Nomination == NominationType.Winner));

            var absoluteJamNumbers = absoluteJams.Select(g => g.Key).ToHashSet();

            model.IdeaWins = wins.Count(w =>
                w.Nomination == NominationType.Idea &&
                !absoluteJamNumbers.Contains(w.AnimJamNumber));

            model.TechnicalWins = wins.Count(w =>
                w.Nomination == NominationType.Technical &&
                !absoluteJamNumbers.Contains(w.AnimJamNumber));

            model.WinnerWins = wins.Count(w => w.Nomination == NominationType.Winner);
            model.HonorableWins = wins.Count(w => w.Nomination == NominationType.HonorableMention);

            return model;
        }

        private async Task<UserWorksViewModel> BuildWorksModel(ulong discordId, string? fallbackName, string tab = "anijam")
        {
            var connectedIds = await GetConnectedIdsAsync(discordId);
            var allIds = new HashSet<ulong>(connectedIds) { discordId };

            var allRawParticipations = await _db.Participants
                .Where(p => allIds.Contains(p.DiscordUserId))
                .OrderByDescending(p => p.AnimJamNumber)
                .ToListAsync();

            var allWins = await _db.Winners
                .Where(w => allIds.Contains(w.DiscordUserId))
                .ToListAsync();

            var targetType = tab == "artjam" ? EventType.ArtJam : EventType.AniJam;

            var rawParticipations = allRawParticipations
                .Where(p => p.EventType == targetType)
                .ToList();

            var participations = rawParticipations
                .GroupBy(p => new { p.AnimJamNumber, p.WorkUrl })
                .Select(g => g.First())
                .OrderByDescending(p => p.AnimJamNumber)
                .ToList();

            var wins = allWins
                .Where(w => w.EventType == targetType)
                .ToList();

            var jamKeys = participations
                .Select(p => (p.EventType, p.AnimJamNumber))
                .Distinct()
                .ToList();

            var allJams = await _db.AnimJams
                .Select(j => new { j.Number, j.Theme, j.EventType })
                .ToListAsync();

            var jams = allJams
                .Where(j => jamKeys.Contains((j.EventType, j.Number)))
                .ToDictionary(j => (j.EventType, j.Number), j => j.Theme);

            var displayName = fallbackName;
            if (string.IsNullOrEmpty(displayName))
                displayName = await ResolveDisplayNameAsync(discordId);

            var model = new UserWorksViewModel
            {
                DisplayName = displayName,
                DiscordUserId = discordId,
                Tab = tab,
                AniJamWorksCount = allRawParticipations
                                    .Where(p => p.EventType == EventType.AniJam)
                                    .GroupBy(p => new { p.AnimJamNumber, p.WorkUrl })
                                    .Count(),
                ArtJamWorksCount = allRawParticipations
                                    .Where(p => p.EventType == EventType.ArtJam)
                                    .GroupBy(p => new { p.AnimJamNumber, p.WorkUrl })
                                    .Count()
            };

            foreach (var p in participations)
            {
                // Победы привязываем к конкретной работе по WorkUrl (с учётом всех связанных id)
                var jamWins = wins
                    .Where(w => w.AnimJamNumber == p.AnimJamNumber &&
                                !string.IsNullOrEmpty(w.WorkUrl) &&
                                w.WorkUrl == p.WorkUrl)
                    .ToList();

                // Fallback: если WorkUrl в Winner пуст/устарел — берём все победы по этому аниджему
                if (jamWins.Count == 0)
                    jamWins = wins.Where(w => w.AnimJamNumber == p.AnimJamNumber).ToList();

                var item = new UserWorkItem
                {
                    Id = p.Id,
                    AnimJamNumber = p.AnimJamNumber,
                    Theme = jams.GetValueOrDefault((p.EventType, p.AnimJamNumber), ""),
                    EventType = p.EventType,
                    WorkUrl = p.WorkUrl,
                    MessageUrl = p.MessageUrl,
                    Nomination = jamWins
                                .Where(w => w.Nomination != NominationType.HonorableMention)
                                .Select(w => (NominationType?)w.Nomination)
                                .FirstOrDefault(),
                    NominationDisplay = BuildNominationDisplay(jamWins, p.AnimJamNumber),
                    NominationDisplayHtml = BuildNominationDisplayHtml(jamWins, p.AnimJamNumber),
                    BadgeHtml = BuildBadgeHtml(jamWins, p.AnimJamNumber),
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
                    item.ThumbnailUrl = $"/Media/StreamWork/{p.Id}";
                }
                else if (Regex.IsMatch(p.WorkUrl, @"\.(gif|png|jpg|jpeg)(\?|$)", RegexOptions.IgnoreCase))
                {
                    item.MediaType = "image";
                    item.ThumbnailUrl = $"/Media/StreamWork/{p.Id}";
                }
                else
                {
                    item.MediaType = "link";
                }

                model.Works.Add(item);
            }

            return model;
        }

        private string BuildNominationDisplay(List<Winner> jamWins, int jamNumber)
        {
            if (jamWins == null || jamWins.Count == 0)
                return "";

            // Если только HonorableMention — показываем его отдельно
            var realWins = jamWins
                .Where(w => w.Nomination != NominationType.HonorableMention)
                .ToList();

            // Абсолютная победа: и Idea, и Technical
            if (realWins.Any(w => w.Nomination == NominationType.Idea) &&
                realWins.Any(w => w.Nomination == NominationType.Technical))
            {
                return $"🏅 Абсолютная победа";
            }

            if (realWins.Count > 0)
                return NominationHelper.GetDisplay(realWins[0].Nomination, jamNumber);

            // Только HonorableMention
            return NominationHelper.GetDisplay(NominationType.HonorableMention, jamNumber);
        }

        private string BuildNominationDisplayHtml(List<Winner> jamWins, int jamNumber)
        {
            if (jamWins == null || jamWins.Count == 0) return "";

            var realWins = jamWins
                .Where(w => w.Nomination != NominationType.HonorableMention)
                .ToList();

            if (realWins.Any(w => w.Nomination == NominationType.Idea) &&
                realWins.Any(w => w.Nomination == NominationType.Technical))
            {
                return $"🏅 Абсолютная победа";
            }

            if (realWins.Count > 0)
                return NominationHelper.GetDisplayHtml(realWins[0].Nomination, jamNumber);

            // Только HonorableMention
            return NominationHelper.GetDisplayHtml(NominationType.HonorableMention, jamNumber);
        }

        private string BuildBadgeHtml(List<Winner> jamWins, int jamNumber)
        {
            if (jamWins == null || jamWins.Count == 0) return "";

            bool hasIdea = jamWins.Any(w => w.Nomination == NominationType.Idea);
            bool hasTech = jamWins.Any(w => w.Nomination == NominationType.Technical);
            bool hasWinner = jamWins.Any(w => w.Nomination == NominationType.Winner);
            bool hasHonorable = jamWins.Any(w => w.Nomination == NominationType.HonorableMention);

            if (hasIdea && hasTech) return EmojiHelper.Trophy;      // абсолютная
            if (hasWinner) return EmojiHelper.Trophy;                // старый формат
            if (hasIdea) return EmojiHelper.Star;
            if (hasTech) return EmojiHelper.Hammer;
            if (hasHonorable) return EmojiHelper.GoldPoopHtml;

            return "";
        }


        // ====== СВЯЗАННЫЕ АККАУНТЫ ======

        [HttpGet]
        public async Task<IActionResult> LinkedAccounts()
        {
            var discordIdStr = User.FindFirst("discord_id")?.Value;
            if (!ulong.TryParse(discordIdStr, out var me))
                return RedirectToAction("Login", "Account");

            var model = new UserLinkedAccountsViewModel();

            model.Pending = await _db.LinkedAccounts
                .Where(la => la.PrimaryDiscordId == me && !la.IsConfirmed)
                .OrderBy(la => la.Id)
                .ToListAsync();

            var connectedIds = await GetConnectedIdsAsync(me);
            var guild = _discordClient.Guilds.Values.FirstOrDefault();

            foreach (var id in connectedIds)
            {
                string? name = null;
                if (guild != null)
                {
                    try
                    {
                        var member = await guild.GetMemberAsync(id);
                        if (member != null) name = member.DisplayName ?? member.Username;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(name))
                    name = await ResolveDisplayNameAsync(id);

                model.Connected.Add(new LinkedAccountDisplay
                {
                    DiscordId = id,
                    Name = name
                });
            }

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> AddLinkedAccount(string input)
        {
            var discordIdStr = User.FindFirst("discord_id")?.Value;
            if (!ulong.TryParse(discordIdStr, out var me))
                return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(input))
            {
                TempData["Error"] = "Введите ID или ник";
                return RedirectToAction(nameof(LinkedAccounts));
            }

            input = input.Trim();
            ulong? targetId = null;
            string? targetName = null;

            if (ulong.TryParse(input, out var parsedId))
            {
                if (parsedId == me)
                {
                    TempData["Error"] = "Нельзя связать аккаунт с самим собой";
                    return RedirectToAction(nameof(LinkedAccounts));
                }

                var exists = await _db.Participants.AnyAsync(p => p.DiscordUserId == parsedId);
                if (!exists)
                {
                    TempData["Error"] = "Указанный ID не найден среди участников";
                    return RedirectToAction(nameof(LinkedAccounts));
                }

                targetId = parsedId;
            }
            else
            {
                targetName = input;

                var myNames = await _db.Participants
                    .Where(p => p.DiscordUserId == me)
                    .Select(p => p.DisplayName)
                    .ToListAsync();

                if (myNames.Any(n => string.Equals(n, targetName, StringComparison.OrdinalIgnoreCase)))
                {
                    TempData["Error"] = "Нельзя связать аккаунт с самим собой";
                    return RedirectToAction(nameof(LinkedAccounts));
                }
            }

            // Проверка на дубль пары в любом направлении
            if (targetId.HasValue)
            {
                var existsEither = await _db.LinkedAccounts.AnyAsync(la =>
                    (la.PrimaryDiscordId == me && la.LinkedDiscordId == targetId) ||
                    (la.PrimaryDiscordId == targetId && la.LinkedDiscordId == me));
                if (existsEither)
                {
                    TempData["Error"] = "Связь между этими аккаунтами уже существует или подана";
                    return RedirectToAction(nameof(LinkedAccounts));
                }
            }

            _db.LinkedAccounts.Add(new LinkedAccount
            {
                PrimaryDiscordId = me,
                LinkedDiscordId = targetId,
                LinkedName = targetName,
                IsConfirmed = false
            });
            await _db.SaveChangesAsync();

            TempData["Result"] = "Заявка на связь отправлена модератору";
            return RedirectToAction(nameof(LinkedAccounts));
        }

        // Все подтверждённые id, достижимые из discordId (не включая сам discordId)
        private async Task<HashSet<ulong>> GetConnectedIdsAsync(ulong discordId)
        {
            var confirmed = await _db.LinkedAccounts
                .Where(la => la.IsConfirmed && la.LinkedDiscordId != null)
                .ToListAsync();

            var result = new HashSet<ulong>();
            var queue = new Queue<ulong>();
            queue.Enqueue(discordId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var link in confirmed)
                {
                    ulong other;
                    if (link.PrimaryDiscordId == current)
                        other = link.LinkedDiscordId!.Value;
                    else if (link.LinkedDiscordId == current)
                        other = link.PrimaryDiscordId;
                    else
                        continue;

                    if (other != discordId && result.Add(other))
                        queue.Enqueue(other);
                }
            }

            return result;
        }
        /// <summary>
        /// Возвращает отображаемое имя: живой Discord-ник → последний ник из БД → "ID x".
        /// </summary>
        private async Task<string> ResolveDisplayNameAsync(ulong discordId)
        {
            var guild = _discordClient.Guilds.Values.FirstOrDefault();
            if (guild != null)
            {
                try
                {
                    var member = await guild.GetMemberAsync(discordId);
                    if (member != null)
                        return member.DisplayName ?? member.Username;
                }
                catch { }
            }

            var dbName = await _db.Participants
                .Where(p => p.DiscordUserId == discordId)
                .OrderByDescending(p => p.AnimJamNumber)
                .Select(p => p.DisplayName)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrEmpty(dbName))
                return dbName;

            return $"██████████";
        }
    }
}