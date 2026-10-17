namespace AnimatorsWeb.Models
{
    public class ChannelSettings
    {
        public int Id { get; set; }

        // Каналы для разных типов публикаций
        public ulong? ThemePollChannelId { get; set; }        // Голосование за тему
        public ulong? AnimJamStartChannelId { get; set; }     // Старт аниджема
        public ulong? WinnerPollChannelId { get; set; }       // Голосование за победителя
        public ulong? WinnersAnnouncementChannelId { get; set; } // Объявление победителей
        public ulong? AnnouncementChannelId { get; set; }     // Общие оповещения
    }
}