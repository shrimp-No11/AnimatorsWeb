using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AnimatorsWeb.Models
{
    public class WinnersPageViewModel
    {
        public int AnimJamNumber { get; set; }
        public string Theme { get; set; } = "";
        public string AnnouncementUrl { get; set; } = "";
        public WinnerPollMode Mode { get; set; }
        public ulong PublishChannelId { get; set; }

        public List<NominationSection> Sections { get; set; } = new();

        [ValidateNever] public string? Message { get; set; }
        [ValidateNever] public List<AnimJam> AllJams { get; set; } = new();
    }

    public class NominationSection
    {
        public NominationType Nomination { get; set; }
        public string NominationDisplay { get; set; } = "";
        public string NominationDisplayHtml { get; set; } = "";   // ← это
        public List<WinnerTeam> Teams { get; set; } = new();
    }

    public class WinnerTeam
    {
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public List<string> Participants { get; set; } = new() { "" };
    }
}