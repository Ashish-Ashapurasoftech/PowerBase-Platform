using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Compute;

namespace PowerBase.Infrastructure.Compute;

public enum GuardDecision { Proceed, Slow, Stop }

/// <summary>The tenant database stayed too busy for the worker: the work is retried later, never forced through.</summary>
public sealed class DatabaseBusyException(string message) : Exception(message);

/// <summary>
/// Keeps the tenant database free for the API (design 10.1). The worker shares nothing else with the API, so the limits live here:
/// - at most <see cref="ComputeOptions.MaxSessionsPerDatabase"/> worker sessions per tenant database, across every replica, held as
///   session-level sp_getapplock slots on the connection the work runs on;
/// - before every batch, sys.dm_db_resource_stats: above the slow threshold wait a little, above the stop threshold back off and retry;
/// - a short pause after every batch so API queries run in between.
/// Azure SQL has no per-query CPU limits, so this cannot be left to the database.
/// </summary>
public static class DatabaseGuard
{
    private const string SlotPrefix = "pb_compute_slot_";
    internal static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);
    internal const int MaxStopWaits = 10;

    /// <summary>The highest of CPU, data IO and log write decides. Unknown load (not Azure SQL) = proceed.</summary>
    public static GuardDecision Decide(double? cpu, double? dataIo, double? logWrite, ComputeOptions options)
    {
        var load = new[] { cpu, dataIo, logWrite }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        if (load >= options.GuardStopPercent) return GuardDecision.Stop;
        if (load >= options.GuardSlowPercent) return GuardDecision.Slow;
        return GuardDecision.Proceed;
    }

    /// <summary>
    /// Opens a connection and takes one of the database's worker slots on it. <paramref name="backgroundOnly"/> work (backfill,
    /// reconciliation) may only take the highest slots (ComputeOptions.BackgroundSessionsPerDatabase), so live changes always have slot 1. Null when no slot frees up within
    /// <paramref name="wait"/>.
    /// </summary>
    public static async Task<GuardedConnection?> AcquireAsync(Func<CancellationToken, Task<SqlConnection>> open, ComputeOptions options,
        bool backgroundOnly, TimeSpan wait, ILogger? logger, CancellationToken ct)
    {
        var connection = await open(ct);
        await connection.OpenAsync(ct);
        // Background work takes only the highest slots (BackgroundSessionsPerDatabase of them): slot 1 always stays free for live changes.
        var first = backgroundOnly
            ? Math.Clamp(options.MaxSessionsPerDatabase - Math.Max(1, options.BackgroundSessionsPerDatabase) + 1, Math.Min(2, options.MaxSessionsPerDatabase), options.MaxSessionsPerDatabase)
            : 1;
        var deadline = DateTime.UtcNow + wait;
        try
        {
            while (true)
            {
                for (var slot = first; slot <= options.MaxSessionsPerDatabase; slot++)
                {
                    var resource = SlotPrefix + slot;
                    var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                        DECLARE @r INT;
                        EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                        SELECT @r;
                        """, new { resource }, cancellationToken: ct));
                    if (result >= 0) return new GuardedConnection(connection, resource, options, logger);
                }
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }
}

/// <summary>A connection holding one worker slot of a tenant database. All of a job's statements run on it.</summary>
public sealed class GuardedConnection : IAsyncDisposable
{
    private const int InvalidObject = 208;
    private readonly string _resource;
    private readonly ComputeOptions _options;
    private readonly ILogger? _logger;
    private bool _statsUnavailable;

    internal GuardedConnection(SqlConnection connection, string resource, ComputeOptions options, ILogger? logger)
    {
        Connection = connection;
        _resource = resource;
        _options = options;
        _logger = logger;
    }

    public SqlConnection Connection { get; }

    /// <summary>Waits until the database has room for one more batch; throws <see cref="DatabaseBusyException"/> when it stays too busy.</summary>
    public async Task BeforeBatchAsync(CancellationToken ct)
    {
        for (var stops = 0; ; stops++)
        {
            var (cpu, io, log) = await ReadLoadAsync(ct);
            switch (DatabaseGuard.Decide(cpu, io, log, _options))
            {
                case GuardDecision.Proceed:
                    return;
                case GuardDecision.Slow:
                    await Task.Delay(DatabaseGuard.SlowWait, ct);
                    return;
                default:
                    if (stops >= DatabaseGuard.MaxStopWaits)
                        throw new DatabaseBusyException($"The database stayed above {_options.GuardStopPercent}% load; the work is retried later.");
                    _logger?.LogInformation("Compute guard: database above {Stop}% load, waiting {Seconds}s.", _options.GuardStopPercent, DatabaseGuard.StopWait.TotalSeconds);
                    await Task.Delay(DatabaseGuard.StopWait, ct);
                    break;
            }
        }
    }

    public Task AfterBatchAsync(CancellationToken ct) =>
        _options.PauseBetweenBatchesMs > 0 ? Task.Delay(_options.PauseBetweenBatchesMs, ct) : Task.CompletedTask;

    private async Task<(double? Cpu, double? Io, double? Log)> ReadLoadAsync(CancellationToken ct)
    {
        if (_statsUnavailable) return (null, null, null);
        try
        {
            var row = await Connection.QuerySingleOrDefaultAsync<(decimal? Cpu, decimal? Io, decimal? Log)?>(new CommandDefinition("""
                SELECT TOP (1) avg_cpu_percent, avg_data_io_percent, avg_log_write_percent
                FROM sys.dm_db_resource_stats ORDER BY end_time DESC
                """, cancellationToken: ct));
            return row is { } r ? ((double?)r.Cpu, (double?)r.Io, (double?)r.Log) : (null, null, null);
        }
        catch (SqlException ex) when (ex.Number == InvalidObject)
        {
            _statsUnavailable = true;   // not Azure SQL (local SQL Server): only the slot limit and the pause apply
            return (null, null, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Connection.State == System.Data.ConnectionState.Open)
                await Connection.ExecuteAsync("EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", new { resource = _resource });
        }
        catch { /* closing the session releases the lock anyway */ }
        await Connection.DisposeAsync();
    }
}
