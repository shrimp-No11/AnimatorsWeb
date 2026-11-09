using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AnimatorsWeb.Models
{
    public class ArtJamWinnerPollViewModel
    {
        public int ArtJamNumber { get; set; }
        public string PollTitle { get; set; } = "";
        public ulong ChannelId { get; set; }

        [ValidateNever]
        public SelectList ChannelList { get; set; }

        public int DurationHours { get; set; } = 24;

        public List<GuildMemberOption> GuildMembers { get; set; } = new();
        public List<ArtJamWorkItem> Works { get; set; } = new();
        public string? Message { get; set; }
    }

    public class ArtJamWorkItem
    {
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public List<ArtJamPerson> People { get; set; } = new();
    }

    public class ArtJamPerson
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
    }
}