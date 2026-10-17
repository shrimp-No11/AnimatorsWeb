using Microsoft.AspNetCore.Mvc.Rendering;

namespace AnimatorsWeb.Models
{
    public class ChannelSettingsViewModel
    {
        public ChannelSettings Settings { get; set; }
        public SelectList ChannelList { get; set; }
    }
}