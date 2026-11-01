namespace AnimatorsWeb.Models
{
    public class WinnerPollMessage
    {
        public int Id { get; set; }
        public int WinnerPollId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public int Order { get; set; }
    }
}