using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class MicrosoftConnectionRepository : TenantRepositoryBase, IMicrosoftConnectionRepository
{
    public MicrosoftConnectionRepository(ITenantConnectionFactory tenantConnectionFactory, IQueryContext queryContext)
        : base(tenantConnectionFactory, queryContext) { }

    private const string COLUMNS = @"
            Id, PublicId, TenantId, CreatedByUserId, Provider, MicrosoftUserId, AccountEmail, DisplayName, Scopes,
            EncryptedRefreshToken, EncryptedAccessToken, AccessTokenExpiresAt, Status, LastError,
            CreatedAt, ModifiedAt, LastUsedAt, IsDeleted, DeletedAt, RowVersion";

    private const string INSERTED_COLUMNS = @"
            INSERTED.Id, INSERTED.PublicId, INSERTED.TenantId, INSERTED.CreatedByUserId, INSERTED.Provider,
            INSERTED.MicrosoftUserId, INSERTED.AccountEmail, INSERTED.DisplayName, INSERTED.Scopes,
            INSERTED.EncryptedRefreshToken, INSERTED.EncryptedAccessToken, INSERTED.AccessTokenExpiresAt,
            INSERTED.Status, INSERTED.LastError, INSERTED.CreatedAt, INSERTED.ModifiedAt, INSERTED.LastUsedAt,
            INSERTED.IsDeleted, INSERTED.DeletedAt, INSERTED.RowVersion";

    private const string LIST_FOR_USER_SQL = "SELECT" + COLUMNS + @"
        FROM meta.MicrosoftConnection
        WHERE TenantId = @tenantId AND CreatedByUserId = @userId AND Provider = @provider AND IsDeleted = 0
        ORDER BY DisplayName, AccountEmail, Id;";

    private const string GET_BY_PUBLIC_ID_SQL = "SELECT" + COLUMNS + @"
        FROM meta.MicrosoftConnection
        WHERE TenantId = @tenantId AND CreatedByUserId = @userId AND PublicId = @publicId AND IsDeleted = 0;";

    private const string GET_BY_MICROSOFT_USER_SQL = "SELECT" + COLUMNS + @"
        FROM meta.MicrosoftConnection
        WHERE TenantId = @tenantId AND CreatedByUserId = @userId AND Provider = @provider
          AND MicrosoftUserId = @microsoftUserId AND IsDeleted = 0;";

    private const string INSERT_SQL = @"
        INSERT INTO meta.MicrosoftConnection
            (PublicId, TenantId, CreatedByUserId, Provider, MicrosoftUserId, AccountEmail, DisplayName, Scopes,
             EncryptedRefreshToken, EncryptedAccessToken, AccessTokenExpiresAt, Status, LastError)
        OUTPUT" + INSERTED_COLUMNS + @"
        VALUES
            (NEWID(), @TenantId, @CreatedByUserId, @Provider, @MicrosoftUserId, @AccountEmail, @DisplayName, @Scopes,
             @EncryptedRefreshToken, @EncryptedAccessToken, @AccessTokenExpiresAt, @Status, @LastError);";

    private const string UPDATE_SQL = @"
        UPDATE meta.MicrosoftConnection
        SET AccountEmail = @AccountEmail, DisplayName = @DisplayName, Scopes = @Scopes,
            EncryptedRefreshToken = @EncryptedRefreshToken, EncryptedAccessToken = @EncryptedAccessToken,
            AccessTokenExpiresAt = @AccessTokenExpiresAt, Status = @Status, LastError = @LastError,
            LastUsedAt = @LastUsedAt, ModifiedAt = SYSUTCDATETIME()
        OUTPUT" + INSERTED_COLUMNS + @"
        WHERE Id = @Id AND TenantId = @TenantId AND IsDeleted = 0 AND RowVersion = @RowVersion;";

    private const string SOFT_DELETE_SQL = @"
        UPDATE meta.MicrosoftConnection
        SET IsDeleted = 1, DeletedAt = SYSUTCDATETIME(), EncryptedRefreshToken = N'', EncryptedAccessToken = NULL
        WHERE Id = @Id AND TenantId = @tenantId AND IsDeleted = 0 AND RowVersion = @RowVersion;";

    public async Task<IReadOnlyList<MicrosoftConnection>> ListForUserAsync(long userId, string provider, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<MicrosoftConnection>(new CommandDefinition(LIST_FOR_USER_SQL,
            new { tenantId = QueryContext.TenantId, userId, provider }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<MicrosoftConnection?> GetByPublicIdForUserAsync(Guid publicId, long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MicrosoftConnection>(new CommandDefinition(GET_BY_PUBLIC_ID_SQL,
            new { tenantId = QueryContext.TenantId, userId, publicId }, cancellationToken: ct));
    }

    public async Task<MicrosoftConnection?> GetByMicrosoftUserAsync(long userId, string provider, string microsoftUserId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MicrosoftConnection>(new CommandDefinition(GET_BY_MICROSOFT_USER_SQL,
            new { tenantId = QueryContext.TenantId, userId, provider, microsoftUserId }, cancellationToken: ct));
    }

    public async Task<MicrosoftConnection> CreateAsync(MicrosoftConnection row, CancellationToken ct = default)
    {
        row.TenantId = QueryContext.TenantId;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<MicrosoftConnection>(new CommandDefinition(INSERT_SQL, row, cancellationToken: ct));
    }

    public async Task<MicrosoftConnection> UpdateAsync(MicrosoftConnection row, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var updated = await connection.QuerySingleOrDefaultAsync<MicrosoftConnection>(new CommandDefinition(UPDATE_SQL,
            new
            {
                row.Id, TenantId = QueryContext.TenantId, row.RowVersion, row.AccountEmail, row.DisplayName, row.Scopes,
                row.EncryptedRefreshToken, row.EncryptedAccessToken, row.AccessTokenExpiresAt, row.Status, row.LastError, row.LastUsedAt
            }, cancellationToken: ct));
        return updated ?? throw new Domain.Exceptions.ConcurrencyException(nameof(MicrosoftConnection));
    }

    public async Task<bool> SoftDeleteAsync(MicrosoftConnection row, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(SOFT_DELETE_SQL,
            new { row.Id, tenantId = QueryContext.TenantId, row.RowVersion }, cancellationToken: ct)) == 1;
    }
}
