using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>Reads (never writes) the source app's app tokens. Resolved inside a source-tenant scope, so the query runs
/// against the source tenant's database.</summary>
public sealed class ConnectedAppTokenReader(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedAppTokenReader
{
    private const string IsValidSql = """
        SELECT COUNT(1) FROM meta.AppToken
        WHERE AppId = @appId AND TokenHash = @tokenHash AND IsActive = 1 AND IsDeleted = 0
        """;

    public async Task<bool> IsValidAsync(long appId, string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(IsValidSql, new { appId, tokenHash }, cancellationToken: ct)) > 0;
    }
}
