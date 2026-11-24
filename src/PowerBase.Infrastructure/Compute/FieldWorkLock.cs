using Dapper;
using Microsoft.Data.SqlClient;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// One worker at a time per stored field, across every replica: a session-level sp_getapplock on the tenant database, held on its own
/// connection for as long as the field is built, refreshed or checked. A replica that cannot take it at once skips the field (another one is
/// on it) and moves to the next, so replicas share the work instead of doing it twice. Closing the connection releases the lock, also when
/// the process dies.
/// </summary>
public sealed class FieldWorkLock : IAsyncDisposable
{
    private const string Prefix = "pb_compute_field_";
    private readonly SqlConnection _connection;
    private readonly string _resource;

    private FieldWorkLock(SqlConnection connection, string resource)
    {
        _connection = connection;
        _resource = resource;
    }

    /// <summary>The lock, or null when another worker holds it right now.</summary>
    public static async Task<FieldWorkLock?> TryAcquireAsync(Func<CancellationToken, Task<SqlConnection>> open, long fieldId, CancellationToken ct)
    {
        var connection = await open(ct);
        try
        {
            await connection.OpenAsync(ct);
            var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                DECLARE @r INT;
                EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @r;
                """, new { resource = Prefix + fieldId }, cancellationToken: ct));
            if (result >= 0) return new FieldWorkLock(connection, Prefix + fieldId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    /// <summary>Released explicitly: a pooled connection is not closed on the server, so its session (and lock) would otherwise live on.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
                await _connection.ExecuteAsync("EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", new { resource = _resource });
        }
        catch { /* the session ends with the connection anyway */ }
        await _connection.DisposeAsync();
    }
}
