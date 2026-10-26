using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>A session-owned SQL application lock in the destination tenant DB, held on its own connection for the whole refresh.</summary>
public sealed class ConnectedTableLock(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedTableLock
{
    private const string AcquireSql = """
        DECLARE @res INT;
        EXEC @res = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
        SELECT @res;
        """;

    public async Task<IAsyncDisposable?> TryAcquireAsync(long connectedTableId, CancellationToken ct = default)
    {
        var conn = await ConnectionFactory.CreateAsync(ct);
        try
        {
            await conn.OpenAsync(ct);
            var name = $"PB_ConnectedTable_{connectedTableId}";
            var result = await conn.QuerySingleAsync<int>(new CommandDefinition(AcquireSql, new { name }, cancellationToken: ct));
            if (result < 0)
            {
                await conn.DisposeAsync();
                return null;
            }
            return new Held(conn, name);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Held(SqlConnection conn, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await conn.ExecuteAsync("EXEC sp_releaseapplock @Resource = @name, @LockOwner = 'Session';", new { name });
            }
            catch (SqlException) { /* closing the connection releases a session lock anyway */ }
            finally { await conn.DisposeAsync(); }
        }
    }
}
