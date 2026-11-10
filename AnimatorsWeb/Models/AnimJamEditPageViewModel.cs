namespace AnimatorsWeb.Models
{
    public class AnimJamEditPageViewModel
    {
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public List<EditWorkItem> Works { get; set; } = new();
        public List<GuildMemberOption> GuildMembers { get; set; } = new();
        public List<DbMemberOption> DbMembers { get; set; } = new();
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public EventType EventType { get; set; } = EventType.AniJam;
        public string EventTypeLabel => EventType == EventType.ArtJam ? "ArtJam" : "AniJam";
    }
    public class DbMemberOption
    {
        public ulong Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class EditWorkItem
    {
        public int GroupKey { get; set; }  // Id первого Participant группы (0 = новая работа)
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public NominationEditType Nomination { get; set; } = NominationEditType.Participant;
        public List<EditPerson> People { get; set; } = new();
        public int PrimaryAttachmentIndex { get; set; } = 0;
    }

    public class EditPerson
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public enum NominationEditType
    {
        Participant = 0,      // просто участник (нет победы)
        Idea = 1,             // лучшая идея
        Technical = 2,        // лучшее техническое исполнение
        Winner = 3,           // победитель (старый формат)
        Absolute = 4,         // абсолютный (Idea + Technical)
        HonorableMention = 5  // почётное упоминание
    }
}