using System.Threading;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Счётчик «календарь изменился». Нужен, чтобы открытая страница календаря
    /// узнавала об изменениях без опроса Discord: gateway/Apply/локальные правки
    /// увеличивают версию, страница раз в полминуты спрашивает только это число
    /// (in-memory, без БД и без Discord API) и перезагружается при расхождении.
    /// </summary>
    public static class CalendarChangeNotifier
    {
        private static long _version;

        /// <summary>Текущая версия. Меняется при любом изменении календаря.</summary>
        public static long Version => Interlocked.Read(ref _version);

        public static void Bump() => Interlocked.Increment(ref _version);
    }
}
