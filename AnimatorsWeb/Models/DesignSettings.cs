namespace AnimatorsWeb.Models
{
    public class DesignSettings
    {
        public int Id { get; set; }

        // Фон страницы (очень светлый)
        public string BackgroundColor { get; set; } = "#FAF7F2";

        // Основной текст (очень тёмный)
        public string TextColor { get; set; } = "#1A1A1A";

        // Акцент — кнопки, ссылки
        public string PrimaryColor { get; set; } = "#E8743B";

        // Hover акцента (чуть темнее)
        public string PrimaryHoverColor { get; set; } = "#C95E28";

        // Яркий цвет для бейджей/декора
        public string AccentColor { get; set; } = "#F4C95D";

        // Фон карточек (почти белый)
        public string CardColor { get; set; } = "#FFFFFF";

        // Когда последний раз обновляли палитру из аватарки
        public DateTime? PaletteUpdatedAt { get; set; }
    }
}