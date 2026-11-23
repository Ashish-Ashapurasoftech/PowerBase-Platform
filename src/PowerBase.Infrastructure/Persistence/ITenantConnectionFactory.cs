using Microsoft.Data.SqlClient;

namespace PowerBase.Infrastructure.Persistence;

/// <summary>Who is asking for a read-only connection, which decides how far behind the replica may be.</summary>
public enum ReadReplicaIntent
{
    /// <summary>A user is waiting (export, summary report): the replica may be at most ReadReplica:MaxLagSeconds behind.</summary>
    Interactive = 0,
    /// <summary>Backfill reads, reconciliation, search indexing: ReadReplica:BackgroundMaxLagSeconds.</summary>
    Background = 1
}

public interface ITenantConnectionFactory
{
    /// <summary>A connection to the tenant's PRIMARY database. Every write, every transaction, all metadata and anything that must be
    /// exactly current uses this.</summary>
    Task<SqlConnection> CreateAsync(CancellationToken ct = default);

    /// <summary>
    /// A connection for heavy reads that may be a little behind (exports, summary reports, background reads). Goes to the tenant's read
    /// replica only when ReadReplica:Enabled AND the tenant's ReadReplicaEnabled are on AND the replica's heartbeat is fresh enough for
    /// <paramref name="intent"/>; in every other case it is a primary connection, exactly like <see cref="CreateAsync"/>. Never use it
    /// for live compute outbox events, writes or metadata. Pair it with <see cref="ReadReplicaFallback"/> so a query that fails on the
    /// replica (a column added a moment ago) is retried on the primary.
    /// </summary>
    Task<SqlConnection> CreateReadOnlyAsync(ReadReplicaIntent intent = ReadReplicaIntent.Interactive, CancellationToken ct = default) => CreateAsync(ct);
}
