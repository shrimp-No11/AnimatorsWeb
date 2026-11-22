using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Проверка модераторских ролей Discord. Вызывается при логине и один раз в день
    /// при первом заходе пользователя на сайт — так смена роли подхватывается без перелогина.
    /// </summary>
    public class ModeratorRoleService
    {
        public const string ModeratorClaim = "is_moderator";
        public const string DiscordIdClaim = "discord_id";

        // Cookie с датой последней проверки, чтобы не дёргать Discord на каждый запрос.
        public const string CheckDateCookie = "moderator_check_date";
        public const string CheckDateFormat = "yyyy-MM-dd";

        public const ulong GuildId = 1007550083627233290;
        public const ulong PresidentRoleId = 1007550169526587392;
        public const ulong AssistantRoleId = 1325850174194581556;

        private static readonly TimeSpan FailureCacheTime = TimeSpan.FromMinutes(5);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;

        public ModeratorRoleService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
        }

        public static bool IsModerator(ClaimsPrincipal? user) =>
            user?.HasClaim(ModeratorClaim, "true") == true;

        /// <summary>
        /// null — проверить не удалось (сеть/401), true/false — уверенный ответ Discord.
        /// Успешный результат кешируется до конца дня, неудачный — на 5 минут.
        /// </summary>
        public async Task<bool?> CheckAsync(string accessToken, string discordId)
        {
            var date = DateTime.Today.ToString(CheckDateFormat);
            var successKey = $"moderator-check:{discordId}:{date}";
            var failureKey = $"moderator-check-fail:{discordId}:{date}";

            if (_cache.TryGetValue(successKey, out bool cached)) return cached;
            if (_cache.TryGetValue(failureKey, out bool failed) && failed) return null;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await client.GetAsync(
                    $"https://discord.com/api/v10/users/@me/guilds/{GuildId}/member");

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("roles", out var roles)) return null;

                bool isModerator = false;
                foreach (var role in roles.EnumerateArray())
                {
                    if (ulong.TryParse(role.GetString(), out var roleId) &&
                        (roleId == PresidentRoleId || roleId == AssistantRoleId))
                    {
                        isModerator = true;
                        break;
                    }
                }

                _cache.Set(successKey, isModerator, DateTime.Today.AddDays(1));
                return isModerator;
            }
            catch
            {
                _cache.Set(failureKey, true, DateTime.UtcNow.Add(FailureCacheTime));
                return null;
            }
        }

        /// <summary>Меняет клейм в уже существующем принципале (для текущего запроса).</summary>
        public static void Apply(ClaimsPrincipal principal, bool isModerator)
        {
            if (principal.Identity is not ClaimsIdentity identity) return;

            var claim = identity.FindFirst(ModeratorClaim);
            if (claim != null) identity.RemoveClaim(claim);

            identity.AddClaim(new Claim(ModeratorClaim, isModerator ? "true" : "false"));
        }
    }
}
