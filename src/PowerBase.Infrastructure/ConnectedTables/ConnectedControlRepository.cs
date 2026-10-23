using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>Control-DB storage for the module. meta.Tenant is only read; everything written lives in the 'connected' schema.</summary>
public sealed class ConnectedControlRepository(IControlConnectionFactory connections, IQueryContext context)
    : ControlRepositoryBase(connections, context), IConnectedControlRepository
{
    private const string TenantColumns = "Id, Slug, Name, Status, ProvisioningState, ServerRef, IsDeleted";
    private const string GetTenantSql = $"SELECT {TenantColumns} FROM meta.Tenant WHERE Id = @tenantId";
    private const string GetTenantBySlugSql = $"SELECT {TenantColumns} FROM meta.Tenant WHERE Slug = @slug AND IsDeleted = 0";
    private const string GetTenantNamesSql = "SELECT Id, Name FROM meta.Tenant WHERE Id IN @ids";

    private const string GetPolicySql = """
        SELECT TenantId, OutboundPolicy, InboundEnabled, ModifiedOn, ModifiedBy, RowVersion
        FROM connected.SharingPolicy WHERE TenantId = @tenantId
        """;

    private const string SavePolicySql = """
        MERGE connected.SharingPolicy WITH (HOLDLOCK) AS t
        USING (SELECT @TenantId AS TenantId) AS s ON t.TenantId = s.TenantId
        WHEN MATCHED THEN UPDATE SET OutboundPolicy = @OutboundPolicy, InboundEnabled = @InboundEnabled, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
        WHEN NOT MATCHED THEN INSERT (TenantId, OutboundPolicy, InboundEnabled, ModifiedBy) VALUES (@TenantId, @OutboundPolicy, @InboundEnabled, @ModifiedBy);
        """;

    private const string ShareColumns = """
        Id, PublicId, ProviderTenantId, ConsumerTenantId, Scope, AppPublicIdsJson, AllowEncryptedFields, AllowCrossRegion, Status,
        RequestedByUserId, RequestedOn, RequestNote, DecidedByUserId, DecidedOn, ExpiresOn, RowVersion
        """;
    private const string GetShareSql = $"SELECT {ShareColumns} FROM connected.DataShare WHERE PublicId = @publicId";
    private const string GetOpenShareSql = $"""
        SELECT TOP 1 {ShareColumns} FROM connected.DataShare
        WHERE ProviderTenantId = @providerTenantId AND ConsumerTenantId = @consumerTenantId AND Status IN ('requested', 'approved')
        ORDER BY Id DESC
        """;
    private const string ListForProviderSql = $"SELECT {ShareColumns} FROM connected.DataShare WHERE ProviderTenantId = @tenantId ORDER BY Id DESC";
    private const string ListForConsumerSql = $"SELECT {ShareColumns} FROM connected.DataShare WHERE ConsumerTenantId = @tenantId ORDER BY Id DESC";

    private const string InsertShareSql = """
        INSERT INTO connected.DataShare (ProviderTenantId, ConsumerTenantId, Scope, AppPublicIdsJson, Status, RequestedByUserId, RequestNote)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@ProviderTenantId, @ConsumerTenantId, @Scope, @AppPublicIdsJson, @Status, @RequestedByUserId, @RequestNote)
        """;

    private const string UpdateShareSql = """
        UPDATE connected.DataShare
        SET Scope = @Scope, AppPublicIdsJson = @AppPublicIdsJson, AllowEncryptedFields = @AllowEncryptedFields, AllowCrossRegion = @AllowCrossRegion,
            Status = @Status, DecidedByUserId = @DecidedByUserId, DecidedOn = @DecidedOn, ExpiresOn = @ExpiresOn
        WHERE Id = @Id AND RowVersion = @RowVersion
        """;

    private const string LinkColumns = """
        Id, PublicId, ConsumerTenantId, ConsumerConnectedTablePublicId, ConsumerTableName, ProviderTenantId, ProviderAppPublicId,
        ProviderTablePublicId, ProviderTableName, RunAsUserId, Status, LastRunOn, LastRowCount, CreatedOn
        """;

    private const string UpsertLinkSql = """
        MERGE connected.Link WITH (HOLDLOCK) AS t
        USING (SELECT @ConsumerTenantId AS ConsumerTenantId, @ConsumerConnectedTablePublicId AS ConsumerConnectedTablePublicId) AS s
            ON t.ConsumerTenantId = s.ConsumerTenantId AND t.ConsumerConnectedTablePublicId = s.ConsumerConnectedTablePublicId
        WHEN MATCHED THEN UPDATE SET ConsumerTableName = @ConsumerTableName, ProviderTenantId = @ProviderTenantId,
            ProviderAppPublicId = @ProviderAppPublicId, ProviderTablePublicId = @ProviderTablePublicId, ProviderTableName = @ProviderTableName,
            RunAsUserId = @RunAsUserId, Status = @Status
        WHEN NOT MATCHED THEN INSERT (ConsumerTenantId, ConsumerConnectedTablePublicId, ConsumerTableName, ProviderTenantId, ProviderAppPublicId,
            ProviderTablePublicId, ProviderTableName, RunAsUserId, Status)
            VALUES (@ConsumerTenantId, @ConsumerConnectedTablePublicId, @ConsumerTableName, @ProviderTenantId, @ProviderAppPublicId,
            @ProviderTablePublicId, @ProviderTableName, @RunAsUserId, @Status);
        """;

    private const string GetLinkSql = $"""
        SELECT {LinkColumns} FROM connected.Link
        WHERE ConsumerTenantId = @consumerTenantId AND ConsumerConnectedTablePublicId = @publicId
        """;
    private const string GetLinkByPublicIdSql = $"SELECT {LinkColumns} FROM connected.Link WHERE PublicId = @publicId";
    private const string ListLinksForProviderSql = $"SELECT {LinkColumns} FROM connected.Link WHERE ProviderTenantId = @tenantId AND Status <> 'removed' ORDER BY Id DESC";
    private const string ListLinksForConsumerSql = $"SELECT {LinkColumns} FROM connected.Link WHERE ConsumerTenantId = @tenantId AND Status <> 'removed' ORDER BY Id DESC";
    private const string SetLinkStatusSql = "UPDATE connected.Link SET Status = @status WHERE Id = @linkId";
    private const string SetLinkStatusForPairSql = """
        UPDATE connected.Link SET Status = @toStatus
        WHERE ProviderTenantId = @providerTenantId AND ConsumerTenantId = @consumerTenantId AND Status = @fromStatus
        """;
    private const string RecordLinkRunSql = """
        UPDATE connected.Link SET LastRunOn = SYSUTCDATETIME(), LastRowCount = @rowCount
        WHERE ConsumerTenantId = @consumerTenantId AND ConsumerConnectedTablePublicId = @publicId
        """;

    public async Task<ConnectedTenantInfo?> GetTenantAsync(long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedTenantInfo>(new CommandDefinition(GetTenantSql, new { tenantId }, cancellationToken: ct));
    }

    public async Task<ConnectedTenantInfo?> GetTenantBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedTenantInfo>(new CommandDefinition(GetTenantBySlugSql, new { slug }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<long, string>> GetTenantNamesAsync(IReadOnlyCollection<long> tenantIds, CancellationToken ct = default)
    {
        if (tenantIds.Count == 0) return new Dictionary<long, string>();
        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<(long Id, string Name)>(new CommandDefinition(GetTenantNamesSql, new { ids = tenantIds.Distinct().ToList() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task<ConnectedSharingPolicy> GetPolicyAsync(long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedSharingPolicy>(new CommandDefinition(GetPolicySql, new { tenantId }, cancellationToken: ct))
            ?? new ConnectedSharingPolicy { TenantId = tenantId };
    }

    public async Task SavePolicyAsync(ConnectedSharingPolicy policy, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SavePolicySql, policy, cancellationToken: ct));
    }

    public async Task<ConnectedDataShare?> GetShareAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedDataShare>(new CommandDefinition(GetShareSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<ConnectedDataShare?> GetOpenShareAsync(long providerTenantId, long consumerTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedDataShare>(new CommandDefinition(GetOpenShareSql, new { providerTenantId, consumerTenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedDataShare>> ListSharesForProviderAsync(long providerTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ConnectedDataShare>(new CommandDefinition(ListForProviderSql, new { tenantId = providerTenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<ConnectedDataShare>> ListSharesForConsumerAsync(long consumerTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ConnectedDataShare>(new CommandDefinition(ListForConsumerSql, new { tenantId = consumerTenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<(long Id, Guid PublicId)> CreateShareAsync(ConnectedDataShare share, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleAsync<(long, Guid)>(new CommandDefinition(InsertShareSql, share, cancellationToken: ct));
    }

    public async Task UpdateShareAsync(ConnectedDataShare share, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(UpdateShareSql, share, cancellationToken: ct));
        if (affected == 0) throw new ConcurrencyException("DataShare");
    }

    public async Task UpsertLinkAsync(ConnectedLink link, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(UpsertLinkSql, link, cancellationToken: ct));
    }

    public async Task<ConnectedLink?> GetLinkAsync(long consumerTenantId, Guid consumerConnectedTablePublicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedLink>(new CommandDefinition(GetLinkSql,
            new { consumerTenantId, publicId = consumerConnectedTablePublicId }, cancellationToken: ct));
    }

    public async Task<ConnectedLink?> GetLinkByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedLink>(new CommandDefinition(GetLinkByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedLink>> ListLinksForProviderAsync(long providerTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ConnectedLink>(new CommandDefinition(ListLinksForProviderSql, new { tenantId = providerTenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<ConnectedLink>> ListLinksForConsumerAsync(long consumerTenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        return (await conn.QueryAsync<ConnectedLink>(new CommandDefinition(ListLinksForConsumerSql, new { tenantId = consumerTenantId }, cancellationToken: ct))).AsList();
    }

    public async Task SetLinkStatusAsync(long linkId, string status, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetLinkStatusSql, new { linkId, status }, cancellationToken: ct));
    }

    public async Task SetLinkStatusForPairAsync(long providerTenantId, long consumerTenantId, string fromStatus, string toStatus, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetLinkStatusForPairSql,
            new { providerTenantId, consumerTenantId, fromStatus, toStatus }, cancellationToken: ct));
    }

    public async Task RecordLinkRunAsync(long consumerTenantId, Guid consumerConnectedTablePublicId, long rowCount, CancellationToken ct = default)
    {
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(RecordLinkRunSql,
            new { consumerTenantId, publicId = consumerConnectedTablePublicId, rowCount }, cancellationToken: ct));
    }
}
