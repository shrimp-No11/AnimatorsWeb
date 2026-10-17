using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AnimatorsWeb.Models
{
    public enum WinnerPollMode
    {
        Combined,
        TwoNominations
    }

    public class WinnerPollViewModel
    {
        public int AnimJamNumber { get; set; }
        public WinnerPollMode Mode { get; set; } = WinnerPollMode.TwoNominations;

        public string PollTitle { get; set; } = "";
        public string IdeaTitle { get; set; } = "";
        public string TechnicalTitle { get; set; } = "";

        public ulong ChannelId { get; set; }
        public int DurationHours { get; set; } = 24;

        public List<PollSection> Sections { get; set; } = new();

        [ValidateNever] public SelectList ChannelList { get; set; }
        [ValidateNever] public string? Message { get; set; }

        // Для автозаполнения имён
        [ValidateNever] public List<GuildMemberOption> GuildMembers { get; set; } = new();
    }

    public class PollSection
    {
        public NominationType Nomination { get; set; }
        public string NominationDisplay { get; set; } = "";
        public List<PollTeam> Teams { get; set; } = new();
    }

    public class PollTeam
    {
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public List<PollParticipant> Participants { get; set; } = new();
    }

    public class PollParticipant
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public class GuildMemberOption
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = "";
    }
}