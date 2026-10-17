namespace AnimatorsWeb.Models
{
    public class Winner
    {
        public int Id { get; set; }
        public int AnimJamNumber { get; set; }
        public ulong DiscordUserId { get; set; }
        public NominationType Nomination { get; set; }
        public string WorkUrl { get; set; } = "";
    }

    public enum NominationType
    {
        Winner,
        Technical,
        Idea,
        HonorableMention
    }
}