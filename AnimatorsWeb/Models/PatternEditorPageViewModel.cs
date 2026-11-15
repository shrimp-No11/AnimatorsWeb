namespace AnimatorsWeb.Models
{
    public class PatternEditorPageViewModel
    {
        public EventPattern Pattern { get; set; } = new();
        public List<EventPreset> Presets { get; set; } = new();
        public List<GuildMemberOption> VoiceChannels { get; set; } = new();
        public bool IsNew { get; set; }
    }
}