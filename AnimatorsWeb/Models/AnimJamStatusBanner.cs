namespace AnimatorsWeb.Models
{
    public class AnimJamStatusBanner
    {
        public string Kind { get; set; } = "";        // active | voting-theme | voting-winners | ended | closed
        public string Text { get; set; } = "";
        public string? EndDateUtc { get; set; }        // для «работы принимаются до …»
    }
}