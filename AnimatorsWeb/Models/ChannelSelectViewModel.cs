using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
namespace AnimatorsWeb.Models
{
    
    public class ChannelSelectViewModel
    {
        [ValidateNever]
        public SelectList ChannelList { get; set; }

        public ulong SelectedChannelId { get; set; }
    }
}