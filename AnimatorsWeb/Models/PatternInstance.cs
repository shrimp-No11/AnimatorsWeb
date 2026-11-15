namespace AnimatorsWeb.Models
{
    public class PatternInstance
    {
        public int Id { get; set; }
        public int EventPatternId { get; set; }
        public EventPattern? EventPattern { get; set; }
        public DateTime StartWeekUtc { get; set; }          
        public DateTime MaterializedUntilUtc { get; set; }  
        public bool IsActive { get; set; } = true;
    }
}