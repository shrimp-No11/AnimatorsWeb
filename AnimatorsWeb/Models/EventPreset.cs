namespace AnimatorsWeb.Models
{
    public class EventPreset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string DefaultTitle { get; set; } = "";
        public string? DefaultDescription { get; set; }
        public int DurationHours { get; set; } = 2;
        public string? Location { get; set; }
        public bool CreateInDiscord { get; set; } = true;


        public bool IsRecurring { get; set; }
        public int? RecurrenceFrequency { get; set; }   // 0=yearly,1=monthly,2=weekly,3=daily
        public int RecurrenceInterval { get; set; } = 1;
        public string? RecurrenceByWeekdays { get; set; } // CSV: "5" = Friday (0=Mon..6=Sun)

        public TimeSpan? DefaultStartTimeUtc { get; set; }
        public string? Color { get; set; }


        public bool IsDiscordSeries { get; set; }
        public ulong? DiscordSeriesEventId { get; set; }
        public string? RecurrenceRuleJson { get; set; }
        public string? RecurrenceExceptionsJson { get; set; }

        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// Есть локальные правки (сетка/настройки серии), которые ещё не отправлены в Discord.
        /// Автоматический Pull такие правки не затирает; ручная кнопка «Обновить из Discord» — затирает.
        /// </summary>
        public bool PendingApply { get; set; }

        /// <summary>Обложка события: внешняя ссылка...</summary>
        public string? CoverImageUrl { get; set; }

        /// <summary>...или загруженный файл в wwwroot/uploads/covers.</summary>
        public string? CoverImageFileName { get; set; }

    }
}