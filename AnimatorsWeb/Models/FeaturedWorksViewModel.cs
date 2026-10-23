namespace AnimatorsWeb.Models
{
    public class FeaturedWorksViewModel
    {
        public List<FeaturedWorkItem> Selected { get; set; } = new();
        public List<FeaturedWorkItem> Available { get; set; } = new();
    }

    public class FeaturedWorkItem
    {
        public int? FeaturedId { get; set; }
        public int ParticipantId { get; set; }
        public int AnimJamNumber { get; set; }
        public string DisplayName { get; set; } = "";
        public string WorkUrl { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public bool HasLocalThumbnail { get; set; }
    }
}