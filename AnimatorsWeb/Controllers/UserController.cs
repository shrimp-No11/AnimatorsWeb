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

        public async Task<IActionResult> Profile()
        {
            var discordIdStr = User.FindFirst("discord_id")?.Value;
            if (string.IsNullOrEmpty(discordIdStr) || !ulong.TryParse(discordIdStr, out ulong discordId))
                return RedirectToAction("Login", "Account");

            var model = await BuildProfileModel(discordId, User.Identity?.Name);
            return View(model);
        }

        [AllowAnonymous]
        public async Task<IActionResult> Works(ulong? id = null)
        {
            ulong discordId;

            if (id.HasValue)
            {
                // Просмотр чужих работ — публичный доступ
                discordId = id.Value;
            }
            else
            {
                // Свои работы — нужна авторизация
                if (!User.Identity?.IsAuthenticated ?? true)
                    return RedirectToAction("Login", "Account");

                var discordIdStr = User.FindFirst("discord_id")?.Value;
                if (!ulong.TryParse(discordIdStr, out discordId))
                    return RedirectToAction("Login", "Account");
            }

            var model = await BuildWorksModel(discordId, null);
            return View(model);
        }

        [AllowAnonymous]
        public async Task<IActionResult> PublicProfile(ulong id)
        {
            var model = await BuildProfileModel(id, null);
            return View("Profile", model);
        }

        // === Общие методы ===

        private async Task<UserProfileViewModel> BuildProfileModel(ulong discordId, string? fallbackName)
        {
            var model = new UserProfileViewModel { DiscordUserId = discordId };

            var guild = _discordClient.Guilds.Values.FirstOrDefault();
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
                    }
                }
                catch
                {
                    model.DisplayName = fallbackName ?? $"ID {discordId}";
                }
            }
            else
            {
                model.DisplayName = fallbackName ?? $"ID {discordId}";
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
                if (!string.IsNullOrEmpty(name))
                    model.LinkedAccounts.Add(new LinkedAccountDisplay { DiscordId = id, Name = name });
            }

            // Все id для статистики — связанные + сам
            var allIds = new HashSet<ulong>(connectedIds) { discordId };

            // === Участия ===
            var participations = await _db.Participants
                .Where(p => allIds.Contains(p.DiscordUserId))
                .ToListAsync();

            model.ParticipationsCount = participations
                .Select(p => p.AnimJamNumber)
                .Distinct()
                .Count();

            if (participations.Any())
                model.FirstParticipationDate = participations.Min(p => p.AnimJamNumber);

            // === Победы ===
            var wins = await _db.Winners
                .Where(w => allIds.Contains(w.DiscordUserId))
                .ToListAsync();

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

        private async Task<UserWorksViewModel> BuildWorksModel(ulong discordId, string? fallbackName)
        {
            var connectedIds = await GetConnectedIdsAsync(discordId);
            var allIds = new HashSet<ulong>(connectedIds) { discordId };

            // === Участия (связанные + сам) ===
            var rawParticipations = await _db.Participants
                .Where(p => allIds.Contains(p.DiscordUserId))
                .OrderByDescending(p => p.AnimJamNumber)
                .ToListAsync();

            // Дедуп по (AnimJamNumber, WorkUrl): если несколько связанных указаны авторами одной работы — одна карточка
            var participations = rawParticipations
                .GroupBy(p => new { p.AnimJamNumber, p.WorkUrl })
                .Select(g => g.First())
                .OrderByDescending(p => p.AnimJamNumber)
                .ToList();

            // === Победы (связанные + сам) ===
            var wins = await _db.Winners
                .Where(w => allIds.Contains(w.DiscordUserId))
                .ToListAsync();

            var jamNumbers = participations.Select(p => p.AnimJamNumber).Distinct().ToList();
            var jams = await _db.AnimJams
                .Where(j => jamNumbers.Contains(j.Number))
                .ToDictionaryAsync(j => j.Number, j => j.Theme);

            var displayName = fallbackName;
            if (string.IsNullOrEmpty(displayName))
            {
                var guild = _discordClient.Guilds.Values.FirstOrDefault();
                if (guild != null)
                {
                    try
                    {
                        var member = await guild.GetMemberAsync(discordId);
                        if (member != null)
                            displayName = member.DisplayName;
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(displayName))
                    displayName = $"ID {discordId}";
            }

            var model = new UserWorksViewModel { DisplayName = displayName };

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
                    Theme = jams.GetValueOrDefault(p.AnimJamNumber, ""),
                    WorkUrl = p.WorkUrl,
                    MessageUrl = p.MessageUrl,
                    Nomination = jamWins
                        .Where(w => w.Nomination != NominationType.HonorableMention)
                        .Select(w => (NominationType?)w.Nomination)
                        .FirstOrDefault(),
                    NominationDisplay = BuildNominationDisplay(jamWins, p.AnimJamNumber),
                    NominationDisplayHtml = BuildNominationDisplayHtml(jamWins, p.AnimJamNumber),
                    BadgeHtml = BuildBadgeHtml(jamWins, p.AnimJamNumber)
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

        private static string? GetYouTubeId(string url)
        {
            var regex = new Regex(@"(?:youtu\.be/|youtube\.com/(?:watch\?v=|embed/|v/))([a-zA-Z0-9_-]{11})");
            var match = regex.Match(url);
            return match.Success ? match.Groups[1].Value : null;
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
                model.Connected.Add(new LinkedAccountDisplay
                {
                    DiscordId = id,
                    Name = name ?? $"ID {id}"
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
    }
}