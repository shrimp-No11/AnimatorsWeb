using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
namespace AnimatorsWeb.Models
{
    
    public class ChannelSelectViewModel
    {
        [ValidateNever]
        public SelectList ChannelList { get; set; }

        public ulong SelectedChannelId { get; set; }

        /// <summary>Для общих страниц модератора: какой ивент редактируем.</summary>
        [ValidateNever]
        public EventType EventType { get; set; } = EventType.AniJam;
    }
}