namespace AnimatorsWeb.Models
{
    public class ActiveRoleSettings
    {
        public int Id { get; set; }

        public bool IsEnabled { get; set; }
        public ulong RoleId { get; set; }

        // Условие 1: дни с сообщениями
        public bool MessageEnabled { get; set; } = true;
        public int MessageDaysRequired { get; set; } = 2;
        public int MessageWindowDays { get; set; } = 7;

        // Условие 2: дни с заходом в войс
        public bool VoiceEnabled { get; set; } = true;
        public int VoiceDaysRequired { get; set; } = 2;
        public int VoiceWindowDays { get; set; } = 7;

        // Условие 3: участие в ивентах
        public bool EventEnabled { get; set; } = true;
        public int EventRequired { get; set; } = 1;
        public int EventWindowDays { get; set; } = 30;

        // Когда последний раз проверка применялась (null = ещё не было)
        public DateTime? LastCheckUtc { get; set; }
    }
}