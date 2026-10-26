using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

public sealed class ConnectedTableRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedTableRepository
{
    private const string Columns = """
        Id, PublicId, AppId, AppTableId, ConnectionId, OwnerUserId, SourceTenantId, SourceAppPublicId, SourceTablePublicId, SourceTableName,
        IsCrossTenant, RefreshKeySourceFid, RefreshKeyIsAuto, UpdateMode, FilterEnabled, FilterJson, ScheduleJson, NextRunOn, TimeZone,
        NotifyOnFailure, MassDeleteGuardPct, ConsecutiveFailures, MappingVersion, FullRefreshVersion, LastSourceWatermark, WatermarkVersion,
        IncrementalRunCount, Status, AttentionReason, LastSuccessfulRunOn, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, RowVersion
        """;

    private const string FieldColumns = """
        Id, ConnectedTableId, AppFieldId, SourceFid, SourceFieldName, SourceTypeCode, Conversion, SortOrder, State, IsDeleted
        """;

    private const string GetByIdSql = $"SELECT {Columns} FROM connected.ConnectedTable WHERE Id = @id AND IsDeleted = 0";
    private const string GetByPublicIdSql = $"SELECT {Columns} FROM connected.ConnectedTable WHERE PublicId = @publicId AND IsDeleted = 0";
    private const string GetByAppTableIdSql = $"SELECT {Columns} FROM connected.ConnectedTable WHERE AppTableId = @appTableId AND IsDeleted = 0";
    private const string ListByAppSql = $"SELECT {Columns} FROM connected.ConnectedTable WHERE AppId = @appId AND IsDeleted = 0 ORDER BY CreatedOn";
    private const string ListDueSql = $"""
        SELECT TOP (@take) {Columns} FROM connected.ConnectedTable
        WHERE IsDeleted = 0 AND NextRunOn IS NOT NULL AND NextRunOn <= @now AND Status IN ('active', 'needs_attention')
        ORDER BY NextRunOn
        """;
    private const string ListTableIdsSql = """
        SELECT t.Id, t.PublicId FROM connected.ConnectedTable c JOIN meta.AppTable t ON t.Id = c.AppTableId
        WHERE c.IsDeleted = 0
        """;
    private const string ListConnectedFidsSql = """
        SELECT f.Fid FROM connected.ConnectedTable c
        JOIN meta.AppTable t ON t.Id = c.AppTableId
        JOIN connected.ConnectedField cf ON cf.ConnectedTableId = c.Id AND cf.IsDeleted = 0
        JOIN meta.AppField f ON f.Id = cf.AppFieldId
        WHERE c.IsDeleted = 0 AND t.PublicId = @appTablePublicId AND f.Fid IS NOT NULL
        """;

    private const string InsertSql = """
        INSERT INTO connected.ConnectedTable (AppId, AppTableId, ConnectionId, OwnerUserId, SourceTenantId, SourceAppPublicId, SourceTablePublicId,
            SourceTableName, IsCrossTenant, RefreshKeySourceFid, RefreshKeyIsAuto, UpdateMode, FilterEnabled, FilterJson, ScheduleJson, NextRunOn,
            TimeZone, NotifyOnFailure, MassDeleteGuardPct, Status, CreatedBy)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@AppId, @AppTableId, @ConnectionId, @OwnerUserId, @SourceTenantId, @SourceAppPublicId, @SourceTablePublicId,
            @SourceTableName, @IsCrossTenant, @RefreshKeySourceFid, @RefreshKeyIsAuto, @UpdateMode, @FilterEnabled, @FilterJson, @ScheduleJson, @NextRunOn,
            @TimeZone, @NotifyOnFailure, @MassDeleteGuardPct, @Status, @CreatedBy)
        """;

    private const string InsertFieldSql = """
        INSERT INTO connected.ConnectedField (ConnectedTableId, AppFieldId, SourceFid, SourceFieldName, SourceTypeCode, Conversion, SortOrder, State)
        VALUES (@ConnectedTableId, @AppFieldId, @SourceFid, @SourceFieldName, @SourceTypeCode, @Conversion, @SortOrder, @State)
        """;

    private const string UpdateSettingsSql = """
        UPDATE connected.ConnectedTable
        SET ConnectionId = @ConnectionId, OwnerUserId = @OwnerUserId, SourceTableName = @SourceTableName,
            RefreshKeySourceFid = @RefreshKeySourceFid, RefreshKeyIsAuto = @RefreshKeyIsAuto, UpdateMode = @UpdateMode,
            FilterEnabled = @FilterEnabled, FilterJson = @FilterJson, ScheduleJson = @ScheduleJson, NextRunOn = @NextRunOn, TimeZone = @TimeZone,
            NotifyOnFailure = @NotifyOnFailure, MassDeleteGuardPct = @MassDeleteGuardPct, MappingVersion = @MappingVersion,
            FullRefreshVersion = @FullRefreshVersion, Status = @Status, AttentionReason = @AttentionReason, ConsecutiveFailures = @ConsecutiveFailures,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
        WHERE Id = @Id AND RowVersion = @RowVersion AND IsDeleted = 0
        """;

    private const string SetStatusSql = """
        UPDATE connected.ConnectedTable SET Status = @status, AttentionReason = @reason, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string SetNextRunSql = "UPDATE connected.ConnectedTable SET NextRunOn = @nextRunOn WHERE Id = @id AND IsDeleted = 0";

    private const string DisableScheduleSql = """
        UPDATE connected.ConnectedTable
        SET ScheduleJson = @scheduleJson, NextRunOn = NULL, Status = 'needs_attention', AttentionReason = @reason, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string RecordSuccessSql = """
        UPDATE connected.ConnectedTable
        SET LastSuccessfulRunOn = @completedOn, ConsecutiveFailures = 0,
            LastSourceWatermark = CASE WHEN @watermark IS NULL THEN LastSourceWatermark ELSE @watermark END,
            WatermarkVersion = CASE WHEN @watermarkVersion IS NULL THEN WatermarkVersion ELSE @watermarkVersion END,
            IncrementalRunCount = CASE WHEN @incremental = 1 THEN IncrementalRunCount + 1 ELSE 0 END,
            Status = CASE WHEN Status IN ('needs_attention', 'held') THEN 'active' ELSE Status END,
            AttentionReason = CASE WHEN Status IN ('needs_attention', 'held') THEN NULL ELSE AttentionReason END
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string RecordFailureSql = """
        UPDATE connected.ConnectedTable SET ConsecutiveFailures = ConsecutiveFailures + 1
        OUTPUT inserted.ConsecutiveFailures
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE connected.ConnectedTable SET IsDeleted = 1, Status = @status, NextRunOn = NULL, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string ListFieldsSql = $"SELECT {FieldColumns} FROM connected.ConnectedField WHERE ConnectedTableId = @connectedTableId AND IsDeleted = 0 ORDER BY SortOrder, Id";
    private const string SetFieldStateSql = "UPDATE connected.ConnectedField SET State = @state WHERE Id = @id AND IsDeleted = 0";
    private const string RemoveFieldSql = "UPDATE connected.ConnectedField SET IsDeleted = 1 WHERE Id = @id AND IsDeleted = 0";

    private const string ListRefreshRolesSql = "SELECT AppRoleId FROM connected.RefreshPermission WHERE ConnectedTableId = @connectedTableId";
    private const string DeleteRefreshRolesSql = "DELETE FROM connected.RefreshPermission WHERE ConnectedTableId = @connectedTableId";
    private const string InsertRefreshRoleSql = "INSERT INTO connected.RefreshPermission (ConnectedTableId, AppRoleId) VALUES (@connectedTableId, @appRoleId)";

    public async Task<ConnectedTable?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedTable>(new CommandDefinition(GetByIdSql, new { id }, cancellationToken: ct));
    }

    public async Task<ConnectedTable?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedTable>(new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<ConnectedTable?> GetByAppTableIdAsync(long appTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedTable>(new CommandDefinition(GetByAppTableIdSql, new { appTableId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedTable>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedTable>(new CommandDefinition(ListByAppSql, new { appId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<ConnectedTable>> ListDueAsync(DateTime nowUtc, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedTable>(new CommandDefinition(ListDueSql, new { now = nowUtc, take }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<(long Id, Guid PublicId)>> ListConnectedAppTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<(long, Guid)>(new CommandDefinition(ListTableIdsSql, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<int>> ListConnectedFidsAsync(Guid appTablePublicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<int>(new CommandDefinition(ListConnectedFidsSql, new { appTablePublicId }, cancellationToken: ct))).AsList();
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(ConnectedTable table, IReadOnlyList<ConnectedField> fields, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (id, publicId) = await conn.QuerySingleAsync<(long, Guid)>(new CommandDefinition(InsertSql, table, tx, cancellationToken: ct));
        foreach (var f in fields) f.ConnectedTableId = id;
        if (fields.Count > 0) await conn.ExecuteAsync(new CommandDefinition(InsertFieldSql, fields, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (id, publicId);
    }

    public async Task UpdateSettingsAsync(ConnectedTable table, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(UpdateSettingsSql, table, cancellationToken: ct));
        if (affected == 0) throw new ConcurrencyException("ConnectedTable");
    }

    public async Task SetStatusAsync(long id, string status, string? reason, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetStatusSql, new { id, status, reason = ConnectedConnectionRepository.Truncate(reason, 500) }, cancellationToken: ct));
    }

    public async Task SetNextRunAsync(long id, DateTime? nextRunOn, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetNextRunSql, new { id, nextRunOn }, cancellationToken: ct));
    }

    public async Task DisableScheduleAsync(long id, string scheduleJson, string reason, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(DisableScheduleSql,
            new { id, scheduleJson, reason = ConnectedConnectionRepository.Truncate(reason, 500) }, cancellationToken: ct));
    }

    public async Task RecordSuccessAsync(long id, DateTime completedOn, DateTime? watermark, int? watermarkVersion, bool incremental, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(RecordSuccessSql, new { id, completedOn, watermark, watermarkVersion, incremental }, cancellationToken: ct));
    }

    public async Task<int> RecordFailureAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<int>(new CommandDefinition(RecordFailureSql, new { id }, cancellationToken: ct));
    }

    public async Task SoftDeleteAsync(long id, string status, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SoftDeleteSql, new { id, status }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedField>> ListFieldsAsync(long connectedTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedField>(new CommandDefinition(ListFieldsSql, new { connectedTableId }, cancellationToken: ct))).AsList();
    }

    public async Task AddFieldsAsync(long connectedTableId, IReadOnlyList<ConnectedField> fields, CancellationToken ct = default)
    {
        if (fields.Count == 0) return;
        foreach (var f in fields) f.ConnectedTableId = connectedTableId;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(InsertFieldSql, fields, cancellationToken: ct));
    }

    public async Task SetFieldStateAsync(long connectedFieldId, string state, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetFieldStateSql, new { id = connectedFieldId, state }, cancellationToken: ct));
    }

    public async Task RemoveFieldAsync(long connectedFieldId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(RemoveFieldSql, new { id = connectedFieldId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> ListRefreshRoleIdsAsync(long connectedTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<long>(new CommandDefinition(ListRefreshRolesSql, new { connectedTableId }, cancellationToken: ct))).AsList();
    }

    public async Task SetRefreshRoleIdsAsync(long connectedTableId, IReadOnlyCollection<long> appRoleIds, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync(new CommandDefinition(DeleteRefreshRolesSql, new { connectedTableId }, tx, cancellationToken: ct));
        if (appRoleIds.Count > 0)
            await conn.ExecuteAsync(new CommandDefinition(InsertRefreshRoleSql,
                appRoleIds.Distinct().Select(appRoleId => new { connectedTableId, appRoleId }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
