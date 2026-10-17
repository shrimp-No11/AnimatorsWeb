using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    public static class EmojiHelper
    {
        public const string Trophy = "🏆";
        public const string Star = "✨";
        public const string Hammer = "🔨";
        public const string Medal = "🏅";

        public const string GoldPoopName = "187_20240928180406";
        public const ulong GoldPoopId = 1289543462626136075;

        public static string GoldPoopHtml =>
            $"<img src=\"https://cdn.discordapp.com/emojis/{GoldPoopId}.png\" " +
            $"alt=\"poop\" style=\"width:20px;height:20px;vertical-align:middle;\" />";

        public static string GoldPoopDiscord =>
            $"<:{GoldPoopName}:{GoldPoopId}>";
    }
}