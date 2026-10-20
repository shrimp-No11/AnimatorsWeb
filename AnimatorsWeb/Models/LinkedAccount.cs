namespace AnimatorsWeb.Models
{
    public class LinkedAccount
    {
        public int Id { get; set; }
        public ulong PrimaryDiscordId { get; set; }   // кто подал заявку
        public ulong? LinkedDiscordId { get; set; }   // привязываемый (если известен)
        public string? LinkedName { get; set; }       // ник, если id ещё неизвестен
        public bool IsConfirmed { get; set; }
    }
}