namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Spaces QuickBase requests evenly so all workers of one process together stay under the configured rate
/// (default 8/s: 80% of QuickBase's 10–15 req/s app limit, and well under 100 per 10 s per user token).
/// One instance per process; every request waits for its slot.</summary>
public sealed class QbRateLimiter
{
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private DateTime _next = DateTime.MinValue;

    public QbRateLimiter(double requestsPerSecond, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null)
    {
        if (requestsPerSecond is <= 0 or > 15) throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), "Must be above 0 and at most 15 (QuickBase's app limit).");
        _interval = TimeSpan.FromSeconds(1 / requestsPerSecond);
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public Task WaitAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _now();
            var slot = _next > now ? _next : now;
            _next = slot + _interval;
            wait = slot - now;
        }
        return wait > TimeSpan.Zero ? _delay(wait, ct) : Task.CompletedTask;
    }

    /// <summary>After a 429 everyone waits: no request goes out before <paramref name="until"/>.</summary>
    public void PauseUntil(DateTime until)
    {
        lock (_gate)
            if (until > _next) _next = until;
    }
}
