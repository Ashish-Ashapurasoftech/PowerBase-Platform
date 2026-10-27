using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

public sealed class ConnectedConnectionRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedConnectionRepository
{
    private const string Columns = """
        Id, PublicId, OwnerUserId, Name, AuthMode, RealmSlug, SourceTenantId, SourceAppPublicId, SourceAppName, RunAsUserId,
        UserTokenHash, UserTokenPrefix, AppTokenHash, Status, StatusReason, LastVerifiedOn, LastUsedOn, IsDeleted, CreatedOn, ModifiedOn, RowVersion
        """;

    private const string GetByIdSql = $"SELECT {Columns} FROM connected.[Connection] WHERE Id = @id AND IsDeleted = 0";
    private const string GetByPublicIdSql = $"SELECT {Columns} FROM connected.[Connection] WHERE PublicId = @publicId AND IsDeleted = 0";
    private const string ListByOwnerSql = $"SELECT {Columns} FROM connected.[Connection] WHERE OwnerUserId = @ownerUserId AND IsDeleted = 0 ORDER BY Name";

    private const string InsertSql = """
        INSERT INTO connected.[Connection] (OwnerUserId, Name, AuthMode, RealmSlug, SourceTenantId, SourceAppPublicId, SourceAppName, RunAsUserId,
            UserTokenHash, UserTokenPrefix, AppTokenHash, Status, LastVerifiedOn)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@OwnerUserId, @Name, @AuthMode, @RealmSlug, @SourceTenantId, @SourceAppPublicId, @SourceAppName, @RunAsUserId,
            @UserTokenHash, @UserTokenPrefix, @AppTokenHash, @Status, SYSUTCDATETIME())
        """;

    private const string UpdateSql = """
        UPDATE connected.[Connection]
        SET Name = @Name, AuthMode = @AuthMode, RunAsUserId = @RunAsUserId, UserTokenHash = @UserTokenHash, UserTokenPrefix = @UserTokenPrefix,
            AppTokenHash = @AppTokenHash, SourceAppName = @SourceAppName, Status = @Status, StatusReason = @StatusReason,
            LastVerifiedOn = @LastVerifiedOn, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @Id AND RowVersion = @RowVersion AND IsDeleted = 0
        """;

    private const string SetStatusSql = """
        UPDATE connected.[Connection] SET Status = @status, StatusReason = @reason, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string TouchSql = """
        UPDATE connected.[Connection]
        SET LastUsedOn = SYSUTCDATETIME(), LastVerifiedOn = CASE WHEN @verified = 1 THEN SYSUTCDATETIME() ELSE LastVerifiedOn END
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string CountTablesSql = "SELECT COUNT(1) FROM connected.ConnectedTable WHERE ConnectionId = @id AND IsDeleted = 0";

    private const string SoftDeleteSql = """
        UPDATE connected.[Connection] SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @id AND RowVersion = @rowVersion AND IsDeleted = 0
        """;

    public async Task<ConnectedConnection?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedConnection>(new CommandDefinition(GetByIdSql, new { id }, cancellationToken: ct));
    }

    public async Task<ConnectedConnection?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedConnection>(new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedConnection>> ListByOwnerAsync(long ownerUserId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedConnection>(new CommandDefinition(ListByOwnerSql, new { ownerUserId }, cancellationToken: ct))).AsList();
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(ConnectedConnection connection, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleAsync<(long, Guid)>(new CommandDefinition(InsertSql, connection, cancellationToken: ct));
    }

    public async Task UpdateAsync(ConnectedConnection connection, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(UpdateSql, connection, cancellationToken: ct));
        if (affected == 0) throw new ConcurrencyException("Connection");
    }

    public async Task SetStatusAsync(long id, string status, string? reason, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SetStatusSql, new { id, status, reason = Truncate(reason, 500) }, cancellationToken: ct));
    }

    public async Task TouchAsync(long id, bool verified, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(TouchSql, new { id, verified }, cancellationToken: ct));
    }

    public async Task<int> CountTablesUsingAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(CountTablesSql, new { id }, cancellationToken: ct));
    }

    public async Task SoftDeleteAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(SoftDeleteSql, new { id, rowVersion }, cancellationToken: ct));
        if (affected == 0) throw new ConcurrencyException("Connection");
    }

    internal static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}
