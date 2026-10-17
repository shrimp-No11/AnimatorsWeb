namespace AnimatorsWeb.Models
{
    public class ThemePoll
    {
        public int Id { get; set; }
        public int AnimJamNumber { get; set; }       // номер аниджема
        public ulong ChannelId { get; set; }         // канал, где опубликован
        public ulong MessageId { get; set; }         // ID сообщения с опросом
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}