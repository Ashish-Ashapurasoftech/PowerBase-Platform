using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Imports;

public sealed class ImportDefinitionRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IImportDefinitionRepository
{
    /// <summary>The list query's row. The notification list arrives as JSON text and is parsed into the public item.</summary>
    private sealed record ListRow(
        Guid PublicId, string Name, string ImportType, Guid? SourceTableId, string? SourceTableName, int MappingCount,
        bool NeedsAttention, string? AttentionReason, string? LastRunStatus, DateTime? LastRunOn, string? NotifyEmailsJson,
        string? ScheduleJson, DateTime? NextRunOn, string SourceKind, int TableCount);

    public async Task<ImportDefinition?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ImportDefinition>(new CommandDefinition(
            "SELECT * FROM meta.ImportDefinition WHERE PublicId = @publicId AND IsDeleted = 0", new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportDefinitionListItem>> ListByDestinationAsync(long destinationTableId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT d.PublicId, d.Name, d.ImportType, s.PublicId AS SourceTableId, s.Name AS SourceTableName,
                   (SELECT COUNT(*) FROM OPENJSON(d.FieldMappingJson)
                     WHERE ISNULL(JSON_VALUE([value], '$.doNotImport'), 'false') <> 'true') AS MappingCount,
                   d.NeedsAttention, d.AttentionReason, lr.Status AS LastRunStatus, lr.RunOn AS LastRunOn,
                   JSON_QUERY(d.OptionsJson, '$.notifyEmails') AS NotifyEmailsJson,
                   d.ScheduleJson, d.NextRunOn, d.SourceKind,
                   1 + (SELECT COUNT(*) FROM OPENJSON(d.OptionsJson, '$.additionalTargets')) AS TableCount
            FROM meta.ImportDefinition d
            LEFT JOIN meta.AppTable s ON s.Id = d.SourceTableId
            OUTER APPLY (SELECT TOP 1 r.Status, COALESCE(r.CompletedOn, r.StartedOn, r.CreatedOn) AS RunOn
                         FROM meta.ImportRun r WHERE r.ImportDefinitionId = d.Id ORDER BY r.Id DESC) lr
            WHERE d.DestinationTableId = @destinationTableId AND d.IsDeleted = 0
            ORDER BY d.Name
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<ListRow>(new CommandDefinition(sql, new { destinationTableId }, cancellationToken: ct));
        return rows.Select(r => new ImportDefinitionListItem(r.PublicId, r.Name, r.ImportType, r.SourceTableId ?? Guid.Empty, r.SourceTableName ?? "An uploaded file", r.MappingCount,
            r.NeedsAttention, r.AttentionReason, r.LastRunStatus, r.LastRunOn, ImportJson.Deserialize<List<string>>(r.NotifyEmailsJson) ?? [],
            ImportJson.Deserialize<ImportSchedule>(r.ScheduleJson) is { } s ? ImportScheduling.Describe(s) + (s.Enabled ? "" : " (paused)") : null,
            r.NextRunOn, r.SourceKind, r.TableCount)).ToList();
    }

    public async Task<IReadOnlyList<ImportDefinition>> ListEntitiesByDestinationAsync(long destinationTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportDefinition>(new CommandDefinition(
            "SELECT * FROM meta.ImportDefinition WHERE DestinationTableId = @destinationTableId AND IsDeleted = 0",
            new { destinationTableId }, cancellationToken: ct))).AsList();
    }

    public async Task<long> CreateAsync(ImportDefinition d, CancellationToken ct = default)
    {
        d.PublicId = Guid.NewGuid();
        const string sql = """
            INSERT INTO meta.ImportDefinition
                (PublicId, AppId, DestinationTableId, SourceKind, SourceTableId, Name, ImportType, MergeKeyFid, ConditionsJson,
                 FieldMappingJson, ColumnRulesJson, OptionsJson, ScheduleJson, NextRunOn, RunAsUserId, CreatedBy)
            OUTPUT INSERTED.Id
            VALUES (@PublicId, @AppId, @DestinationTableId, @SourceKind, @SourceTableId, @Name, @ImportType, @MergeKeyFid, @ConditionsJson,
                    @FieldMappingJson, @ColumnRulesJson, @OptionsJson, @ScheduleJson, @NextRunOn, @RunAsUserId, @CreatedBy)
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        d.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, d, cancellationToken: ct));
        return d.Id;
    }

    public async Task UpdateAsync(ImportDefinition d, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.ImportDefinition
            SET Name = @Name, SourceKind = @SourceKind, SourceTableId = @SourceTableId, ImportType = @ImportType, MergeKeyFid = @MergeKeyFid,
                ConditionsJson = @ConditionsJson, FieldMappingJson = @FieldMappingJson, ColumnRulesJson = @ColumnRulesJson,
                OptionsJson = @OptionsJson, ScheduleJson = @ScheduleJson, NextRunOn = @NextRunOn, RunAsUserId = @RunAsUserId, NeedsAttention = @NeedsAttention,
                AttentionReason = @AttentionReason, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
            WHERE Id = @Id AND IsDeleted = 0
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, d, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, long deletedBy, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportDefinition SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @deletedBy WHERE Id = @id",
            new { id, deletedBy }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportDefinition>> ListDueAsync(DateTime nowUtc, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportDefinition>(new CommandDefinition(
            "SELECT TOP (@take) * FROM meta.ImportDefinition WHERE IsDeleted = 0 AND NextRunOn IS NOT NULL AND NextRunOn <= @nowUtc ORDER BY NextRunOn",
            new { nowUtc, take }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> TryAdvanceScheduleAsync(long id, DateTime expected, DateTime? next, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportDefinition SET NextRunOn = @next WHERE Id = @id AND IsDeleted = 0 AND NextRunOn = @expected",
            new { id, expected, next }, cancellationToken: ct)) > 0;
    }

    public async Task SetAttentionAsync(long id, bool needsAttention, string? reason, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportDefinition SET NeedsAttention = @needsAttention, AttentionReason = @reason WHERE Id = @id",
            new { id, needsAttention, reason }, cancellationToken: ct));
    }
}
