using System.Text.RegularExpressions;

namespace AnimatorsWeb.Services
{
    public static class DiscordLinkParser
    {
        // https://discord.com/channels/{guildId}/{channelId}/{messageId}
        public static (ulong? channelId, ulong? messageId) ParseMessageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (null, null);

            var m = Regex.Match(url, @"discord\.com/channels/\d+/(\d+)/(\d+)");
            if (!m.Success) return (null, null);

            if (ulong.TryParse(m.Groups[1].Value, out var channelId) &&
                ulong.TryParse(m.Groups[2].Value, out var messageId))
            {
                return (channelId, messageId);
            }

            return (null, null);
        }
    }
}