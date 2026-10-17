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

            var participations = await _db.Participants
                .Where(p => p.DiscordUserId == discordId)
                .ToListAsync();

            model.ParticipationsCount = participations
                .Select(p => p.AnimJamNumber)
                .Distinct()
                .Count();

            if (participations.Any())
                model.FirstParticipationDate = participations.Min(p => p.AnimJamNumber);

            var wins = await _db.Winners
                .Where(w => w.DiscordUserId == discordId)
                .ToListAsync();

            // Группируем по аниджему — одна победа = один аниджем
            // Группируем по аниджему — одна победа = один аниджем
            var winsByJam = wins.GroupBy(w => w.AnimJamNumber).ToList();

            // Абсолютные победы — там, где есть и Idea, и Technical
            var absoluteJams = winsByJam
                .Where(g => g.Any(w => w.Nomination == NominationType.Idea) &&
                            g.Any(w => w.Nomination == NominationType.Technical))
                .ToList();

            // Старый формат — там, где есть Winner
            var legacyJams = winsByJam
                .Where(g => g.Any(w => w.Nomination == NominationType.Winner))
                .ToList();

            model.AbsoluteVictoriesCount = absoluteJams.Count;
            model.LegacyVictoriesCount = legacyJams.Count;

            // Уникальные аниджемы с победами (без HonorableMention)
            model.WinsCount = winsByJam.Count(g => g.Any(w =>
                w.Nomination == NominationType.Idea ||
                w.Nomination == NominationType.Technical ||
                w.Nomination == NominationType.Winner));

            // Разбивка по номинациям — без учёта абсолютных
            // Считаем записи, которые НЕ относятся к абсолютным аниджемам
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
            var participations = await _db.Participants
                .Where(p => p.DiscordUserId == discordId)
                .OrderByDescending(p => p.AnimJamNumber)
                .ToListAsync();

            var wins = await _db.Winners
                .Where(w => w.DiscordUserId == discordId)
                .ToListAsync();

            var jamNumbers = participations.Select(p => p.AnimJamNumber).Distinct().ToList();
            var jams = await _db.AnimJams
                .Where(j => jamNumbers.Contains(j.Number))
                .ToDictionaryAsync(j => j.Number, j => j.Theme);


            var displayName = fallbackName;

            // Если имя не передали — тянем из Discord
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

            var model = new UserWorksViewModel
            {
                DisplayName = displayName
            };

            foreach (var p in participations)
            {
                // Все номинации, полученные за этот аниджем
                var jamWins = wins.Where(w => w.AnimJamNumber == p.AnimJamNumber).ToList();

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
                    item.ThumbnailUrl = p.WorkUrl;
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
    }
}