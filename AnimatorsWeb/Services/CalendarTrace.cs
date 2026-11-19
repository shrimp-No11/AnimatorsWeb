namespace AnimatorsWeb.Services
{
    /// <summary>Простой файловый trace для разбора календаря. PIP через env CALENDAR_LOG_PATH.</summary>
    public static class CalendarTrace
    {
        private static readonly object Sync = new();
        private static string? _path;

        /// <summary>
        /// Куда пишем лог. Path фиксируется на старте приложения (content root),
        /// поэтому не зависит от текущего каталога процесса — раньше при запуске
        /// из разных мест появлялись разные (и «пустые») файлы.
        /// </summary>
        public static string PathValue => _path ??= ResolveDefault();

        public static void Initialize(string contentRoot)
        {
            var env = Environment.GetEnvironmentVariable("CALENDAR_LOG_PATH");
            _path = string.IsNullOrWhiteSpace(env)
                ? System.IO.Path.Combine(contentRoot, "logs", "calendar.log")
                : env;
        }

        public static void Write(string tag, string message)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(PathValue);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                lock (Sync)
                {
                    System.IO.File.AppendAllText(
                        PathValue,
                        $"{DateTime.UtcNow:O} [{tag}] {message}{Environment.NewLine}");
                }
            }
            catch { /* логирование не должно ронять фичу */ }
        }

        private static string ResolveDefault()
        {
            var env = Environment.GetEnvironmentVariable("CALENDAR_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs", "calendar.log");
        }
    }
}
