namespace AnimatorsWeb.Models
{
    public class RuleSection
    {
        public string Content { get; set; } = "";
        public bool StartNewMessage { get; set; }   
        public List<RuleAttachment> Attachments { get; set; } = new();
    }
}