using System.Text.Json;
using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>Сравнение правил рекуррентности Discord по смыслу, а не по форматированию JSON.</summary>
    public static class DiscordRuleJson
    {
        /// <summary>Разбирает сохранённое правило Discord. null, если JSON не разобрался.</summary>
        public static DiscordRecurrenceRule? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var start = GetString(root, "start");
                if (string.IsNullOrWhiteSpace(start)) return null;

                var days = Days(root);
                return new DiscordRecurrenceRule
                {
                    Start = start,
                    Frequency = GetInt(root, "frequency"),
                    Interval = Math.Max(1, GetInt(root, "interval")),
                    ByWeekday = days.Length == 0 ? null : days
                };
            }
            catch { return null; }
        }

        public static bool Same(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return a == b;
            try
            {
                using var da = JsonDocument.Parse(a);
                using var db = JsonDocument.Parse(b);
                var ra = da.RootElement;
                var rb = db.RootElement;
                if (ParseStart(GetString(ra, "start")) != ParseStart(GetString(rb, "start"))) return false;
                if (GetInt(ra, "frequency") != GetInt(rb, "frequency")) return false;
                if (GetInt(ra, "interval") != GetInt(rb, "interval")) return false;
                return Days(ra).SequenceEqual(Days(rb));
            }
            catch { return false; }
        }

        private static DateTime? ParseStart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateTime.TryParse(value, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt)
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : null;
        }

        private static string? GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int GetInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

        private static int[] Days(JsonElement e)
            => e.TryGetProperty("by_weekday", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                    .Select(x => x.GetInt32()).ToArray()
                : Array.Empty<int>();
    }
}
