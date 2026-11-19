namespace AnimatorsWeb.Models
{
    /// <summary>Исключение правила Discord-серии, сохранённое в EventPreset.RecurrenceExceptionsJson.</summary>
    public class PresetExceptionRecord
    {
        public DateTime OriginalStartUtc { get; set; }
        public bool IsCanceled { get; set; }
        public DateTime? NewStartUtc { get; set; }
    }
}
