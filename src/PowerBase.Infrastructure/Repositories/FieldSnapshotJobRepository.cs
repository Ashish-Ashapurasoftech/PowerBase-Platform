using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.SnapshotJobs;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Control-DB job table for "Reinitialize" of a Snapshot field (see control/015_field_snapshot_job.sql).</summary>
public sealed class FieldSnapshotJobRepository(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IFieldSnapshotJobRepository
{
    private const string Columns = """
        Id, PublicId, TenantId, TablePublicId, TableName, FieldPublicId, FieldLabel, RequestedByUserId, IsSuperAdmin, TenantRole,
        TotalCount, ProcessedCount, ChangedCount, LastRecordId, Status, CancelRequested, ClaimToken, LastError, CreatedOn, StartedOn, CompletedOn
        """;

    public async Task<Guid> CreateAsync(FieldSnapshotJob job, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        var publicId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO meta.FieldSnapshotJob (TenantId, TablePublicId, TableName, FieldPublicId, FieldLabel, RequestedByUserId, IsSuperAdmin, TenantRole, TotalCount)
            OUTPUT inserted.PublicId
            VALUES (@TenantId, @TablePublicId, @TableName, @FieldPublicId, @FieldLabel, @RequestedByUserId, @IsSuperAdmin, @TenantRole, @TotalCount)
            """, job, cancellationToken: ct));
        FieldSnapshotJobWakeNotifier.Wake();
        return publicId;
    }

    public async Task<FieldSnapshotJob?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FieldSnapshotJob>(new CommandDefinition(
            $"SELECT {Columns} FROM meta.FieldSnapshotJob WHERE PublicId = @publicId AND TenantId = @tenantId",
            new { publicId, tenantId }, cancellationToken: ct));
    }

    public async Task<FieldSnapshotJob?> GetLatestForFieldAsync(long tenantId, Guid fieldPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<FieldSnapshotJob>(new CommandDefinition(
            $"SELECT TOP (1) {Columns} FROM meta.FieldSnapshotJob WHERE TenantId = @tenantId AND FieldPublicId = @fieldPublicId ORDER BY CreatedOn DESC, Id DESC",
            new { tenantId, fieldPublicId }, cancellationToken: ct));
    }

    public async Task<bool> HasActiveForFieldAsync(long tenantId, Guid fieldPublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.FieldSnapshotJob
                WHERE TenantId = @tenantId AND FieldPublicId = @fieldPublicId AND Status IN ('Pending', 'InProgress')) THEN 1 ELSE 0 END
            """, new { tenantId, fieldPublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FieldSnapshotJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default)
    {
        const string sql = """
            ;WITH candidates AS (
                SELECT TOP (@batchSize) Id FROM meta.FieldSnapshotJob WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = 'Pending' OR (Status = 'InProgress' AND LockedUntil < SYSUTCDATETIME())
                ORDER BY CreatedOn, Id
            )
            UPDATE j SET Status = 'InProgress', LockedBy = @workerId, ClaimToken = NEWID(),
                         LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                         StartedOn = ISNULL(j.StartedOn, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.PublicId, inserted.TenantId, inserted.TablePublicId, inserted.TableName, inserted.FieldPublicId,
                   inserted.FieldLabel, inserted.RequestedByUserId, inserted.IsSuperAdmin, inserted.TenantRole, inserted.TotalCount,
                   inserted.ProcessedCount, inserted.ChangedCount, inserted.LastRecordId, inserted.Status, inserted.CancelRequested,
                   inserted.ClaimToken, inserted.LastError, inserted.CreatedOn, inserted.StartedOn, inserted.CompletedOn
            FROM meta.FieldSnapshotJob j JOIN candidates c ON c.Id = j.Id
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<FieldSnapshotJob>(new CommandDefinition(sql, new { batchSize, workerId, leaseSeconds }, cancellationToken: ct))).AsList();
    }

    public async Task<FieldSnapshotJobProgress> ReportProgressAsync(
        long id, string workerId, Guid claimToken, int processedCount, int changedCount, long lastRecordId, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        var cancel = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            UPDATE meta.FieldSnapshotJob
            SET ProcessedCount = @processedCount, ChangedCount = @changedCount, LastRecordId = @lastRecordId,
                LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.CancelRequested
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken AND Status = 'InProgress'
            """, new { id, workerId, claimToken, processedCount, changedCount, lastRecordId, leaseSeconds }, cancellationToken: ct));
        return new FieldSnapshotJobProgress(cancel.HasValue, cancel ?? false);
    }

    public async Task CompleteAsync(
        long id, string workerId, Guid claimToken, string status, int processedCount, int changedCount, string? error, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.FieldSnapshotJob
            SET Status = @status, ProcessedCount = @processedCount, ChangedCount = @changedCount, LastError = @error,
                LockedUntil = NULL, CompletedOn = SYSUTCDATETIME()
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
            """, new { id, workerId, claimToken, status, processedCount, changedCount, error }, cancellationToken: ct));
    }

    public async Task<bool> RequestCancelAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.FieldSnapshotJob
            SET CancelRequested = 1,
                Status = CASE WHEN Status = 'Pending' THEN 'Cancelled' ELSE Status END,
                CompletedOn = CASE WHEN Status = 'Pending' THEN SYSUTCDATETIME() ELSE CompletedOn END
            WHERE PublicId = @publicId AND TenantId = @tenantId AND Status IN ('Pending', 'InProgress')
            """, new { publicId, tenantId }, cancellationToken: ct)) > 0;
    }
}
