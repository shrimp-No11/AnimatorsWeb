using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    public static class NominationHelper
    {
        public static string GetDisplay(NominationType nom, int jamNumber)
        {
            return nom switch
            {
                NominationType.Idea => $"✨ победитель в лучшей идее  ",
                NominationType.Technical => $"🔨 победитель в техническом исполнении  ",
                NominationType.Winner => $"🏆 победитель  ",
                NominationType.HonorableMention => $"{EmojiHelper.GoldPoopDiscord} а золотую какаху забирает  ",
                _ => "Победитель"
            };
        }


        public static string GetDisplayHtml(NominationType nom, int jamNumber)
        {
            return nom switch
            {
                NominationType.Idea => $"✨ победитель в лучшей идее ",
                NominationType.Technical => $"🔨 победитель в техническом исполнении  ",
                NominationType.Winner => $"🏆 победитель  ",
                NominationType.HonorableMention => $"{EmojiHelper.GoldPoopHtml} почётное упоминание  ",
                _ => "Победитель"
            };
        }

        public static string GetCombinedDisplay(int jamNumber)
        {
            return $"✨🔨 победитель в номинациях  ";
        }
    }
}