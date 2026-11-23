using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.ComputeWorker;

/// <summary>Which tenants the worker may touch: ready, not deleted, and with their own switch on. Read from the control database.</summary>
public sealed class ComputeTenantDirectory
{
    private const int InvalidColumnName = 207;
    private readonly IControlConnectionFactory _control;

    public ComputeTenantDirectory(IControlConnectionFactory control) => _control = control;

    /// <summary>Tenants with ComputeEnabled = 1. Empty when the control migration (018) has not run: nothing is switched on yet.</summary>
    public Task<IReadOnlyList<long>> ListComputeTenantsAsync(CancellationToken ct) =>
        ListAsync("SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready' AND ComputeEnabled = 1", ct);

    /// <summary>Tenants with a read replica switched on (ReadReplicaEnabled = 1 and a mode). Their heartbeat is written by this worker.</summary>
    public Task<IReadOnlyList<long>> ListReplicaTenantsAsync(CancellationToken ct) =>
        ListAsync("SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready' AND ReadReplicaEnabled = 1 AND ReadReplicaMode > 0", ct);

    private async Task<IReadOnlyList<long>> ListAsync(string sql, CancellationToken ct)
    {
        try
        {
            await using var conn = _control.Create();
            await conn.OpenAsync(ct);
            return (await conn.QueryAsync<long>(new CommandDefinition(sql, cancellationToken: ct))).AsList();
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            return [];
        }
    }
}

/// <summary>What the worker has seen since it started, for the periodic log line (Phase 2 gate evidence).</summary>
public sealed class ComputeWorkerCounters
{
    private long _messages, _skippedDisabled, _failed, _pendingSeen;

    public void Message(long pending) { Interlocked.Increment(ref _messages); Interlocked.Add(ref _pendingSeen, pending); }
    public void SkippedDisabled() => Interlocked.Increment(ref _skippedDisabled);
    public void Failed() => Interlocked.Increment(ref _failed);

    public (long Messages, long SkippedDisabled, long Failed, long PendingSeen) Snapshot() =>
        (Interlocked.Read(ref _messages), Interlocked.Read(ref _skippedDisabled), Interlocked.Read(ref _failed), Interlocked.Read(ref _pendingSeen));
}
