public class Participant
{
    public int Id { get; set; }
    public int AnimJamNumber { get; set; }
    public ulong DiscordUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string WorkUrl { get; set; } = "";
    public string MessageUrl { get; set; } = "";
}