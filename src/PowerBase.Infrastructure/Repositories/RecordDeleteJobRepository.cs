using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.DeleteJobs;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Control-DB job table for "Delete these records" (see control/014_record_delete_job.sql).</summary>
public sealed class RecordDeleteJobRepository(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IRecordDeleteJobRepository
{
    private const string Columns = """
        Id, PublicId, TenantId, ReportPublicId, TablePublicId, TableName, RequestedByUserId, IsSuperAdmin, TenantRole,
        FilterJson, ExpectedCount, DeletedCount, Status, CancelRequested, ClaimToken, LastError, CreatedOn, StartedOn, CompletedOn
        """;

    public async Task<Guid> CreateAsync(RecordDeleteJob job, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        var publicId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO meta.RecordDeleteJob (TenantId, ReportPublicId, TablePublicId, TableName, RequestedByUserId, IsSuperAdmin, TenantRole, FilterJson, ExpectedCount)
            OUTPUT inserted.PublicId
            VALUES (@TenantId, @ReportPublicId, @TablePublicId, @TableName, @RequestedByUserId, @IsSuperAdmin, @TenantRole, @FilterJson, @ExpectedCount)
            """, job, cancellationToken: ct));
        RecordDeleteJobWakeNotifier.Wake();
        return publicId;
    }

    public async Task<RecordDeleteJob?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RecordDeleteJob>(new CommandDefinition(
            $"SELECT {Columns} FROM meta.RecordDeleteJob WHERE PublicId = @publicId AND TenantId = @tenantId",
            new { publicId, tenantId }, cancellationToken: ct));
    }

    public async Task<bool> HasActiveForTableAsync(long tenantId, Guid tablePublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.RecordDeleteJob
                WHERE TenantId = @tenantId AND TablePublicId = @tablePublicId AND Status IN ('Pending', 'InProgress')) THEN 1 ELSE 0 END
            """, new { tenantId, tablePublicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RecordDeleteJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default)
    {
        const string sql = """
            ;WITH candidates AS (
                SELECT TOP (@batchSize) Id FROM meta.RecordDeleteJob WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = 'Pending' OR (Status = 'InProgress' AND LockedUntil < SYSUTCDATETIME())
                ORDER BY CreatedOn, Id
            )
            UPDATE j SET Status = 'InProgress', LockedBy = @workerId, ClaimToken = NEWID(),
                         LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                         StartedOn = ISNULL(j.StartedOn, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.PublicId, inserted.TenantId, inserted.ReportPublicId, inserted.TablePublicId, inserted.TableName,
                   inserted.RequestedByUserId, inserted.IsSuperAdmin, inserted.TenantRole, inserted.FilterJson, inserted.ExpectedCount,
                   inserted.DeletedCount, inserted.Status, inserted.CancelRequested, inserted.ClaimToken, inserted.LastError,
                   inserted.CreatedOn, inserted.StartedOn, inserted.CompletedOn
            FROM meta.RecordDeleteJob j JOIN candidates c ON c.Id = j.Id
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<RecordDeleteJob>(new CommandDefinition(sql, new { batchSize, workerId, leaseSeconds }, cancellationToken: ct))).AsList();
    }

    public async Task<RecordDeleteJobProgress> ReportProgressAsync(long id, string workerId, Guid claimToken, int deletedCount, int leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        var cancel = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            UPDATE meta.RecordDeleteJob
            SET DeletedCount = @deletedCount, LockedUntil = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.CancelRequested
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken AND Status = 'InProgress'
            """, new { id, workerId, claimToken, deletedCount, leaseSeconds }, cancellationToken: ct));
        return new RecordDeleteJobProgress(cancel.HasValue, cancel ?? false);
    }

    public async Task CompleteAsync(long id, string workerId, Guid claimToken, string status, int deletedCount, string? error, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.RecordDeleteJob
            SET Status = @status, DeletedCount = @deletedCount, LastError = @error, LockedUntil = NULL, CompletedOn = SYSUTCDATETIME()
            WHERE Id = @id AND LockedBy = @workerId AND ClaimToken = @claimToken
            """, new { id, workerId, claimToken, status, deletedCount, error }, cancellationToken: ct));
    }

    public async Task<bool> RequestCancelAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.RecordDeleteJob
            SET CancelRequested = 1,
                Status = CASE WHEN Status = 'Pending' THEN 'Cancelled' ELSE Status END,
                CompletedOn = CASE WHEN Status = 'Pending' THEN SYSUTCDATETIME() ELSE CompletedOn END
            WHERE PublicId = @publicId AND TenantId = @tenantId AND Status IN ('Pending', 'InProgress')
            """, new { publicId, tenantId }, cancellationToken: ct)) > 0;
    }
}
