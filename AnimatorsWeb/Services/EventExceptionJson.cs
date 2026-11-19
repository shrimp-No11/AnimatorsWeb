using System.Text.Json;
using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Чтение/запись исключений одиночной recurring-серии. Поддерживает старый формат
    /// (массив ISO-дат = отмены) и новый (записи с NewStartUtc = переносы).
    /// </summary>
    public static class EventExceptionJson
    {
        public static List<PresetExceptionRecord> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
                var result = new List<PresetExceptionRecord>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        if (TryParseDate(el.GetString(), out var d))
                            result.Add(new PresetExceptionRecord { OriginalStartUtc = d, IsCanceled = true });
                    }
                    else if (el.ValueKind == JsonValueKind.Object)
                    {
                        var rec = JsonSerializer.Deserialize<PresetExceptionRecord>(el.GetRawText());
                        // Мусорные записи от старого парсера (OriginalStartUtc = 0001-01-01)
                        // больше не должны попадать в отправляемые исключения.
                        if (rec != null && rec.OriginalStartUtc > DateTime.UnixEpoch)
                            result.Add(rec);
                    }
                }
                return result;
            }
            catch { return new(); }
        }

        public static string Serialize(IEnumerable<PresetExceptionRecord> records)
            => JsonSerializer.Serialize(records.OrderBy(r => r.OriginalStartUtc).ToList());

        public static void Cancel(List<PresetExceptionRecord> records, DateTime originalUtc)
        {
            var existing = records.FirstOrDefault(r => r.OriginalStartUtc == originalUtc);
            if (existing == null)
            {
                records.Add(new PresetExceptionRecord { OriginalStartUtc = originalUtc, IsCanceled = true });
                return;
            }
            existing.IsCanceled = true;
            existing.NewStartUtc = null;
        }

        public static void Move(List<PresetExceptionRecord> records, DateTime originalUtc, DateTime newStartUtc)
        {
            var existing = records.FirstOrDefault(r => r.OriginalStartUtc == originalUtc);
            if (existing != null)
            {
                existing.IsCanceled = false;
                existing.NewStartUtc = newStartUtc;
                return;
            }
            records.Add(new PresetExceptionRecord
            {
                OriginalStartUtc = originalUtc, IsCanceled = false, NewStartUtc = newStartUtc
            });
        }

        private static bool TryParseDate(string? value, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!DateTime.TryParse(value, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed)) return false;
            date = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }
    }
}
