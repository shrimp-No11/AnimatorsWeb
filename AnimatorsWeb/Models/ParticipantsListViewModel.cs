namespace AnimatorsWeb.Models
{
    public class ParticipantsListViewModel
    {
        public List<ParticipantRow> Rows { get; set; } = new();
    }

    public class ParticipantRow
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";

        public int ParticipationsCount { get; set; }
        public int WorksCount { get; set; }
        public int WinsCount { get; set; }
        public int AbsoluteVictories { get; set; }
        public int IdeaWins { get; set; }
        public int TechnicalWins { get; set; }
        public int LegacyWins { get; set; }
        public int HonorableMentions { get; set; }

        public int FirstAnimJam { get; set; }
        public int LastAnimJam { get; set; }
        public DateTime FirstDate { get; set; }
        public DateTime LastDate { get; set; }
    }
}