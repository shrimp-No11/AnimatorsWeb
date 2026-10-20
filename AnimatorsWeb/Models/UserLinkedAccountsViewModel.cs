namespace AnimatorsWeb.Models
{
    public class UserLinkedAccountsViewModel
    {
        public List<LinkedAccount> Pending { get; set; } = new();
        public List<LinkedAccountDisplay> Connected { get; set; } = new();
    }
}