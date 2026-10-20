namespace AnimatorsWeb.Models
{
    public class DesignSettingsViewModel
    {
        public DesignSettings Design { get; set; } = new();
        public string? AvatarUrl { get; set; }
    }
}