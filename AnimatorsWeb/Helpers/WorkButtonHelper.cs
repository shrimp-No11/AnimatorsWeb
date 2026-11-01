using System.Text.Json;

namespace AnimatorsWeb.Helpers
{
    public static class WorkButtonHelper
    {
        public class Author
        {
            public string DisplayName { get; set; } = "";
            public ulong? DiscordUserId { get; set; }
        }

        /// <summary>
        /// Возвращает готовую строку HTML-атрибутов для кнопки, открывающей модалку.
        /// Выводить через @Html.Raw(...).
        /// </summary>
        public static string GetAttributes(
            int participantId,
            string? workUrl,
            string? messageUrl,
            string? mediaType,
            IEnumerable<Author>? authors = null,
            int? animJamNumber = null,
            string? theme = null)
        {
            var meta = new
            {
                authors = (authors ?? Enumerable.Empty<Author>())
                    .Select(a => new
                    {
                        name = (a.DisplayName ?? "").Trim(),
                        discordId = a.DiscordUserId?.ToString() ?? ""
                    })
                    .Where(a => !string.IsNullOrEmpty(a.name) || !string.IsNullOrEmpty(a.discordId)),
                jamNumber = animJamNumber,
                theme = theme
            };

            var metaJson = JsonSerializer.Serialize(meta);

            return string.Join(" ",
                $"data-work-url=\"{Encode(workUrl)}\"",
                $"data-message-url=\"{Encode(messageUrl)}\"",
                $"data-participant-id=\"{participantId}\"",
                $"data-media-type=\"{Encode(mediaType)}\"",
                $"data-meta=\"{Encode(metaJson)}\"");
        }

        private static string Encode(string? s)
            => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}