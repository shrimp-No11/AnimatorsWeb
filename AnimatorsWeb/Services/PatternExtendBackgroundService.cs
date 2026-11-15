namespace AnimatorsWeb.Services
{
    public class PatternExtendBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PatternExtendBackgroundService> _logger;

        public PatternExtendBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<PatternExtendBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                // 04:00 МСК = 01:00 UTC, + случайные 0..45 минут
                var baseTime = now.Date.AddDays(1).AddHours(1);
                var jitter = TimeSpan.FromMinutes(Random.Shared.Next(0, 46));
                var next = baseTime + jitter;
                if (next <= now) next = next.AddDays(1);

                var delay = next - now;
                _logger.LogInformation(
                    "Pattern extend scheduled for {Next} UTC (in {Hours:F1}h)",
                    next, delay.TotalHours);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var materialization = scope.ServiceProvider.GetRequiredService<PatternMaterializationService>();
                    var created = await materialization.ExtendAllActiveAsync(ct);
                    _logger.LogInformation("Pattern extend: created {Created} events", created);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pattern extend failed");
                }
            }
        }
    }
}