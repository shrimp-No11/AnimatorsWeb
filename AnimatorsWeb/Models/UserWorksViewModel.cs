using System.Collections.Generic;

namespace AnimatorsWeb.Models
{
    public class UserWorksViewModel
    {
        public string DisplayName { get; set; } = "";
        public List<UserWorkItem> Works { get; set; } = new();
        public ulong DiscordUserId { get; set; }
    }

    public class UserWorkItem
    {
        public int Id { get; set; }
        public int AnimJamNumber { get; set; }
        public string Theme { get; set; } = "";
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public NominationType? Nomination { get; set; }
        public string NominationDisplay { get; set; } = "";
        public string NominationDisplayHtml { get; set; } = "";  
        public string BadgeHtml { get; set; } = "";             
        public string ThumbnailUrl { get; set; } = "";
        public string MediaType { get; set; } = "";
    }
}