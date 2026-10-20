namespace AnimatorsWeb.Models
{
    public class UserProfileViewModel
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Username { get; set; } = "";
        public string AvatarUrl { get; set; } = "";

        public int ParticipationsCount { get; set; }

        // Основные счётчики
        public int WinsCount { get; set; }             // уникальных аниджемов с победами
        public int AbsoluteVictoriesCount { get; set; } // победа в обеих номинациях
        public int LegacyVictoriesCount { get; set; }   // старая номинация Winner

        // Разбивка по номинациям (может быть больше, чем WinsCount)
        public int IdeaWins { get; set; }
        public int TechnicalWins { get; set; }
        public int WinnerWins { get; set; }
        public int HonorableWins { get; set; }

        public int FirstParticipationDate { get; set; }

        public List<LinkedAccountDisplay> LinkedAccounts { get; set; } = new();
    }
}