namespace AnimatorsWeb.Models
{
    public class ActiveRolePageViewModel
    {
        public ActiveRoleSettings Settings { get; set; } = new();
        public ActiveRolePreviewResult? Preview { get; set; }
    }
}