using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AnimatorsWeb.Models
{
    public class CreateAnimJamViewModel
    {
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public DateTime EndDate { get; set; }     
        public ulong ChannelId { get; set; }

        [ValidateNever]
        public SelectList ChannelList { get; set; }

        [ValidateNever]
        public string? WinningThemeFromPoll { get; set; }

        [ValidateNever]
        public string? PollError { get; set; }
        [ValidateNever]
        public List<string> TieCandidates { get; set; } = new();
    }
}