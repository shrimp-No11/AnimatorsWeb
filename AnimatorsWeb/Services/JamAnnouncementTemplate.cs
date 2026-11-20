using AnimatorsWeb.Helpers;
using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Шаблон объявления о старте ивента. Пользователь редактирует текст прямо в превью,
    /// а переменные остаются токенами и подставляются при публикации в Discord.
    /// </summary>
    public static class JamAnnouncementTemplate
    {
        public static string Default(EventType type)
        {
            return type == EventType.ArtJam
                ? "{{mentions}}\n" +
                  "{{number}} ArtJam начинается!\n\n" +
                  "Дедлайн - {{deadline}}\n" +
                  "Тема - **{{theme}}**\n\n" +
                  "Правила:\n" +
                  "1) Следовать теме\n" +
                  "2) Новая работа, созданная специально для ArtJam\n" +
                  "3) Главное - интересность и креативность\n\n" +
                  "Скидывать работы в этот канал.\n" +
                  "Комментарии к работам - в ветках\n" +
                  "По итогу будет выбран топ лучших работ"
                : "{{mentions}}\n" +
                  "{{number}} AniJam начинается!\n\n" +
                  "Дедлайн - {{deadline}}\n" +
                  "Тема - **{{theme}}**\n\n" +
                  "Правила:\n" +
                  "1) Следовать теме (хоть как-то)\n" +
                  "2) Новая анимация, сделана специально для AniJam\n" +
                  "3) Звук - желательно, не обязательно\n" +
                  "4) Главное - веселье, а не качество\n\n" +
                  "Номинации:\n" +
                  "{{star}} Лучшая идея\n" +
                  "{{hammer}} Лучшее техническое исполнение\n" +
                  "{{goldpoop}} Золотая какаха\n\n" +
                  "Скидывать работы в этот канал.\n" +
                  "Комментарии к анимациям - в ветках\n" +
                  "*Анимации могут быть использованы в видеобзоре аниджема для ютуб-канала \"Сообщество Аниматоров\"";
        }

        public static string Mentions(EventType type) => type == EventType.ArtJam
            ? $"<@&{Roles.roleArtistId}>"
            : $"<@&{Roles.role2dId}>  <@&{Roles.role3dId}>";

        public static string Render(string? template, EventType type, int number, string theme, DateTime endUtc)
        {
            var source = string.IsNullOrWhiteSpace(template) ? Default(type) : template;
            long endUnix = new DateTimeOffset(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

            string ReplaceToken(string name, string arg)
            {
                var values = new Dictionary<string, string>
                {
                    ["number"] = number.ToString(),
                    ["theme"] = theme ?? "",
                    ["deadline"] = $"<t:{endUnix}:F> (<t:{endUnix}:R>)",
                    ["mentions"] = Mentions(type),
                    ["star"] = EmojiHelper.Star,
                    ["hammer"] = EmojiHelper.Hammer,
                    ["goldpoop"] = EmojiHelper.GoldPoopDiscord,
                    ["emoji"] = arg
                };
                return values.TryGetValue(name, out var value) ? value : "{{" + name + "}}";
            }

            var result = new System.Text.StringBuilder(source.Length);
            for (int i = 0; i < source.Length;)
            {
                int open = source.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0)
                {
                    result.Append(source, i, source.Length - i);
                    break;
                }

                result.Append(source, i, open - i);
                int close = source.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    result.Append("{{");
                    i = open + 2;
                    continue;
                }

                var body = source.Substring(open + 2, close - open - 2);
                int colon = body.IndexOf(':');
                var name = colon < 0 ? body : body.Substring(0, colon);
                var arg = colon < 0 ? "" : body.Substring(colon + 1);
                result.Append(ReplaceToken(name, arg));
                i = close + 2;
            }

            return result.ToString();
        }
    }
}
