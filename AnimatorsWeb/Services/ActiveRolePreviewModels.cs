namespace AnimatorsWeb.Models
{
    public enum ActiveRoleAction
    {
        NoChange = 0,
        Grant = 1,
        Revoke = 2
    }

    public class ActiveRolePreviewItem
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
        public ActiveRoleAction Action { get; set; }

        public bool HasRoleNow { get; set; }
        public bool ShouldHaveRole { get; set; }

        public bool MessageCondition { get; set; }
        public bool VoiceCondition { get; set; }
        public bool EventCondition { get; set; }
    }

    public class ActiveRolePreviewResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool IsFirstRun { get; set; }
        public int TotalMembers { get; set; }
        public int WillGrant { get; set; }
        public int WillRevoke { get; set; }
        public int NoChange { get; set; }
        public List<ActiveRolePreviewItem> Items { get; set; } = new();
    }
}