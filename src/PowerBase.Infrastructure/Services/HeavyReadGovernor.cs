using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Keeps the API's whole-table reads from overwhelming a tenant database (doc 21, generalising the ComputeWorker's DatabaseGuard for
/// the API): at most <see cref="SqlWorkloadOptions.HeavyReadsPerTenantDb"/> heavy batches per tenant DB at once on this instance, and
/// under high Azure SQL load each batch waits a little first, so interactive queries keep getting through. Scaling the API out cannot
/// multiply this past instances × limit. Never throws for load: the request only slows down.
/// </summary>
public sealed class HeavyReadGovernor(IOptions<SqlWorkloadOptions> options, ILogger<HeavyReadGovernor> logger)
{
    private readonly SqlWorkloadOptions _options = options.Value;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<long, (long AtTicks, double? Load)> _loads = new();
    private readonly ConcurrentDictionary<long, bool> _noLoadSignal = new();

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }

    private sealed class NoSlot : IDisposable { public static readonly NoSlot Instance = new(); public void Dispose() { } }

    /// <param name="probe">Reads the database's current load in percent (null = unknown, e.g. not Azure SQL).</param>
    public async Task<IDisposable> EnterAsync(long tenantId, Func<CancellationToken, Task<double?>> probe, CancellationToken ct)
    {
        if (!_options.Enabled) return NoSlot.Instance;
        var limit = Math.Max(1, _options.HeavyReadsPerTenantDb);
        var gate = _gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(limit, limit));
        await gate.WaitAsync(ct);
        try
        {
            var load = await LoadAsync(tenantId, probe, ct);
            if (load >= _options.StopPercent)
                await Task.Delay(Math.Max(0, _options.StopDelayMs), ct);
            else if (load >= _options.SlowPercent)
                await Task.Delay(Math.Max(0, _options.SlowDelayMs), ct);
            return new Slot(gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<double?> LoadAsync(long tenantId, Func<CancellationToken, Task<double?>> probe, CancellationToken ct)
    {
        if (_noLoadSignal.ContainsKey(tenantId)) return null;
        var now = DateTime.UtcNow.Ticks;
        if (_loads.TryGetValue(tenantId, out var cached) && now - cached.AtTicks < TimeSpan.FromSeconds(Math.Max(1, _options.LoadCacheSeconds)).Ticks)
            return cached.Load;
        double? load;
        try { load = await probe(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No sys.dm_db_resource_stats (local SQL Server) or no permission: limit by count only, never ask again.
            logger.LogDebug(ex, "No load signal for tenant {TenantId}; heavy reads are limited by count only", tenantId);
            _noLoadSignal[tenantId] = true;
            return null;
        }
        _loads[tenantId] = (now, load);
        return load;
    }
}
