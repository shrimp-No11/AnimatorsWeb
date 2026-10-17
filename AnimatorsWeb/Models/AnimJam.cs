using System.Reflection;

namespace AnimatorsWeb.Models
{
    public class AnimJam
    {
        public int Id { get; set; }
        public int Number { get; set; }
        public string Theme { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? ResultsMessageUrl { get; set; }
        public string? AnounceMessage { get; set; }
    }
}

