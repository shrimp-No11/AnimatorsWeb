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

        /// <summary>
        /// Редактируемый вопрос опроса за тему с токенами {{event}}, {{number}}, {{emoji:...}}.
        /// Если пусто — используется стандартный вопрос ивента.
        /// </summary>
        public string? PollTitleTemplate { get; set; }
    }
}