using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class PipelineAccountRepository : TenantRepositoryBase, IPipelineAccountRepository
{
    public PipelineAccountRepository(ITenantConnectionFactory tenantConnectionFactory, IQueryContext queryContext)
        : base(tenantConnectionFactory, queryContext) { }

    private const string ListForUserSql = @"
        SELECT
            Id, PublicId, TenantId, CreatedByUserId, Name, AuthMode, Subdomain,
            TargetTenantId, TargetUserId, UserTokenPublicId, TokenHash, TokenPrefix,
            Status, IsActive, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM meta.PipelineAccount
        WHERE TenantId = @tenantId
          AND CreatedByUserId = @userId
          AND IsDeleted = 0
        ORDER BY Name, Id;";

    private const string GetByPublicIdForUserSql = @"
        SELECT
            Id, PublicId, TenantId, CreatedByUserId, Name, AuthMode, Subdomain,
            TargetTenantId, TargetUserId, UserTokenPublicId, TokenHash, TokenPrefix,
            Status, IsActive, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM meta.PipelineAccount
        WHERE TenantId = @tenantId
          AND CreatedByUserId = @userId
          AND PublicId = @publicId
          AND IsDeleted = 0;";

    private const string GetByTokenHashSql = @"
        SELECT
            Id, PublicId, TenantId, CreatedByUserId, Name, AuthMode, Subdomain,
            TargetTenantId, TargetUserId, UserTokenPublicId, TokenHash, TokenPrefix,
            Status, IsActive, CreatedAt, LastUsedAt, IsDeleted, RowVersion
        FROM meta.PipelineAccount
        WHERE TenantId = @tenantId
          AND CreatedByUserId = @userId
          AND TokenHash = @tokenHash
          AND IsDeleted = 0;";

    private const string InsertSql = @"
        INSERT INTO meta.PipelineAccount
            (PublicId, TenantId, CreatedByUserId, Name, AuthMode, Subdomain,
             TargetTenantId, TargetUserId, UserTokenPublicId, TokenHash, TokenPrefix,
             Status, IsActive, CreatedAt, IsDeleted)
        OUTPUT
            INSERTED.Id, INSERTED.PublicId, INSERTED.TenantId, INSERTED.CreatedByUserId,
            INSERTED.Name, INSERTED.AuthMode, INSERTED.Subdomain,
            INSERTED.TargetTenantId, INSERTED.TargetUserId, INSERTED.UserTokenPublicId,
            INSERTED.TokenHash, INSERTED.TokenPrefix, INSERTED.Status, INSERTED.IsActive,
            INSERTED.CreatedAt, INSERTED.LastUsedAt, INSERTED.IsDeleted, INSERTED.RowVersion
        VALUES
            (@PublicId, @TenantId, @CreatedByUserId, @Name, @AuthMode, @Subdomain,
             @TargetTenantId, @TargetUserId, @UserTokenPublicId, @TokenHash, @TokenPrefix,
             @Status, @IsActive, @CreatedAt, 0);";

    private const string RefreshCredentialSql = @"
        UPDATE meta.PipelineAccount
        SET Name              = @Name,
            Subdomain         = @Subdomain,
            TargetTenantId    = @TargetTenantId,
            TargetUserId      = @TargetUserId,
            UserTokenPublicId = @UserTokenPublicId,
            TokenPrefix       = @TokenPrefix,
            Status            = @Status,
            IsActive          = @IsActive
        OUTPUT
            INSERTED.Id, INSERTED.PublicId, INSERTED.TenantId, INSERTED.CreatedByUserId,
            INSERTED.Name, INSERTED.AuthMode, INSERTED.Subdomain,
            INSERTED.TargetTenantId, INSERTED.TargetUserId, INSERTED.UserTokenPublicId,
            INSERTED.TokenHash, INSERTED.TokenPrefix, INSERTED.Status, INSERTED.IsActive,
            INSERTED.CreatedAt, INSERTED.LastUsedAt, INSERTED.IsDeleted, INSERTED.RowVersion
        WHERE Id = @Id
          AND TenantId = @TenantId
          AND IsDeleted = 0
          AND RowVersion = @RowVersion;";

    private const string UpdateStatusSql = @"
        UPDATE meta.PipelineAccount
        SET Status = @status, IsActive = @isActive
        WHERE Id = @id AND TenantId = @tenantId AND IsDeleted = 0;";

    private const string UpdateLastUsedAtSql = @"
        UPDATE meta.PipelineAccount
        SET LastUsedAt = SYSUTCDATETIME()
        WHERE Id = @id AND TenantId = @tenantId AND IsDeleted = 0;";

    public async Task<IReadOnlyList<PipelineAccount>> ListForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<PipelineAccount>(
            new CommandDefinition(ListForUserSql,
                new { tenantId = QueryContext.TenantId, userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<PipelineAccount?> GetByPublicIdForUserAsync(Guid publicId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PipelineAccount>(
            new CommandDefinition(GetByPublicIdForUserSql,
                new { tenantId = QueryContext.TenantId, userId, publicId }, cancellationToken: ct));
    }

    public async Task<PipelineAccount?> GetByTokenHashAsync(string tokenHash, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PipelineAccount>(
            new CommandDefinition(GetByTokenHashSql,
                new { tenantId = QueryContext.TenantId, userId, tokenHash }, cancellationToken: ct));
    }

    public async Task<PipelineAccount> CreateAsync(PipelineAccount account, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<PipelineAccount>(
            new CommandDefinition(InsertSql, account, cancellationToken: ct));
    }

    public async Task<PipelineAccount> RefreshCredentialAsync(PipelineAccount account, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var updated = await connection.QuerySingleOrDefaultAsync<PipelineAccount>(
            new CommandDefinition(RefreshCredentialSql, account, cancellationToken: ct));

        if (updated == null)
            throw new Domain.Exceptions.ConcurrencyException(nameof(PipelineAccount));

        return updated;
    }

    public async Task<int> UpdateStatusAsync(long id, string status, bool isActive, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateStatusSql,
                new { id, status, isActive, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task UpdateLastUsedAtAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateLastUsedAtSql,
                new { id, tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }
}
