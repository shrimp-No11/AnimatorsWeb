namespace AnimatorsWeb.Models
{
    public class RulesSettings
    {
        public int Id { get; set; }
        public ulong GuildId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong RoleId { get; set; }
        public string Content { get; set; } = "";

        // ID последнего сообщения с правилами (под ним реакция)
        public ulong LastMessageId { get; set; }

        // ID всех сообщений с правилами (если разбито на несколько)
        public string AllMessageIds { get; set; } = ""; // "123,456,789"
        public string SectionsJson { get; set; } = "";
    }
}