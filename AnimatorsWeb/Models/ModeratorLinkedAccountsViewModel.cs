namespace AnimatorsWeb.Models
{
    public class ModeratorLinkedAccountsViewModel
    {
        public List<LinkedAccountRow> Rows { get; set; } = new();
    }

    public class LinkedAccountRow
    {
        public LinkedAccount Link { get; set; } = null!;
        public string? PrimaryName { get; set; }
        public string? LinkedNameResolved { get; set; }
    }

}