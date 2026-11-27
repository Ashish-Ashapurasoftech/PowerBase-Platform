using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// <see cref="ICacheVersionStore"/> over <c>meta.CacheVersion</c>: the tenant database for a tenant, the control database for
/// <see cref="MetadataCacheGroups.GlobalTenant"/>. Always the primary (never the read replica): these versions decide whether a
/// cached permission may still be used.
/// </summary>
public sealed class SqlCacheVersionStore : ICacheVersionStore
{
    // AgeMs on the database clock (the same clock for every instance), so "changed at least N seconds ago" never depends on a server's time.
    private const string ReadSql = "SELECT Grp, V, DATEDIFF_BIG(MILLISECOND, ChangedOn, SYSUTCDATETIME()) AS AgeMs FROM meta.CacheVersion";

    private const string BumpSql = """
        UPDATE meta.CacheVersion SET V = V + 1, ChangedOn = SYSUTCDATETIME() WHERE Grp = @grp;
        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                INSERT INTO meta.CacheVersion (Grp, V) VALUES (@grp, 1);
            END TRY
            BEGIN CATCH
                -- Another instance inserted the row in between (primary key): bump it instead.
                IF ERROR_NUMBER() IN (2627, 2601) UPDATE meta.CacheVersion SET V = V + 1, ChangedOn = SYSUTCDATETIME() WHERE Grp = @grp;
                ELSE THROW;
            END CATCH
        END
        """;

    private readonly IControlConnectionFactory _control;
    private readonly ITenantConnectionResolver _tenants;
    private readonly ILogger<SqlCacheVersionStore> _logger;

    public SqlCacheVersionStore(IControlConnectionFactory control, ITenantConnectionResolver tenants, ILogger<SqlCacheVersionStore> logger)
    {
        _control = control;
        _tenants = tenants;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, long>?> ReadAsync(long tenantId, CancellationToken ct = default) =>
        (await ReadWithAgeAsync(tenantId, ct))?.ToDictionary(r => r.Key, r => r.Value.Version, StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, CacheGroupVersion>?> ReadWithAgeAsync(long tenantId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(tenantId, ct);
            var rows = await connection.QueryAsync<(string Grp, long V, long AgeMs)>(new CommandDefinition(ReadSql, cancellationToken: ct));
            return rows.ToDictionary(r => r.Grp, r => new CacheGroupVersion(r.V, Math.Max(0, r.AgeMs)), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache versions of tenant {TenantId} could not be read; security-relevant cache groups are bypassed", tenantId);
            return null;
        }
    }

    public bool Bump(long tenantId, string group)
    {
        try
        {
            // Synchronous ADO.NET (no sync-over-async): metadata writes are rare, and the bump must be committed before the write runs.
            using var connection = tenantId == MetadataCacheGroups.GlobalTenant
                ? _control.Create()
                : new SqlConnection(_tenants.ResolveAsync(tenantId).GetAwaiter().GetResult());
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();
            connection.Execute(BumpSql, new { grp = group });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache version of tenant {TenantId} group {Group} could not be bumped; other instances see the change only after their own TTL", tenantId, group);
            return false;
        }
    }

    private async Task<SqlConnection> OpenAsync(long tenantId, CancellationToken ct)
    {
        var connection = tenantId == MetadataCacheGroups.GlobalTenant
            ? _control.Create()
            : new SqlConnection(await _tenants.ResolveAsync(tenantId, ct));
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        return connection;
    }
}
