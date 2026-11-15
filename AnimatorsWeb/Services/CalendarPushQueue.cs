using System.Threading.Channels;

namespace AnimatorsWeb.Services
{
    public class CalendarPushQueue
    {
        private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

        public ValueTask EnqueueAsync(int calendarEventId) =>
            _channel.Writer.WriteAsync(calendarEventId);

        public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);
    }
}