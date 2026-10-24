namespace AnimatorsWeb.Models
{
    public class RuleAttachment
    {
        public string FileName { get; set; } = "";    // отображаемое имя
        public string StoredName { get; set; } = "";  // имя файла на диске (guid.ext)
    }
}