using System.Collections.Generic;

namespace AnimatorsWeb.Models
{
    public class UserWorksViewModel
    {
        public string DisplayName { get; set; } = "";
        public List<UserWorkItem> Works { get; set; } = new();
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
        public string NominationDisplayHtml { get; set; } = "";   // ← новое
        public string BadgeHtml { get; set; } = "";               // ← новое
        public string ThumbnailUrl { get; set; } = "";
        public string MediaType { get; set; } = "";
    }
}