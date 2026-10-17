using System.Collections.Generic;

namespace AnimatorsWeb.Models
{
    public class AnimJamDetailsViewModel
    {
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string AnnouncementUrl { get; set; } = "";
        public string ResultsUrl { get; set; } = "";

        public int ParticipantsCount { get; set; }
        public int WinnersCount { get; set; }

        public List<WinnerGroup> Winners { get; set; } = new();
        public List<ParticipantWork> Participants { get; set; } = new();
    }

    public class WinnerGroup
    {
        public string NominationDisplay { get; set; } = "";
        public string NominationDisplayHtml { get; set; } = "";   // ← это
        public List<WinnerWork> Winners { get; set; } = new();
    }

    public class WinnerWork
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
    }

    public class ParticipantWork
    {
        public int Id { get; set; }                       // ID основной записи (для прокси)
        public List<ParticipantPerson> People { get; set; } = new();  // ← вместо DisplayName/DiscordUserId
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public bool IsWinner { get; set; }
        public string BadgeHtml { get; set; } = "";
    }

    public class ParticipantPerson
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
    }
}