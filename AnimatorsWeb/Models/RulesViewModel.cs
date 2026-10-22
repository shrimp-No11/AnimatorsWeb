using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AnimatorsWeb.Models
{
    public class RulesViewModel
    {
        public RulesSettings Settings { get; set; } = new();

        [ValidateNever]
        public SelectList ChannelList { get; set; }
        public List<RuleSection> Sections { get; set; } = new();
    }
}