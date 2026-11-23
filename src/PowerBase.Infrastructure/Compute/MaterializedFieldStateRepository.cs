using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Compute;

/// <summary>See <see cref="IMaterializedFieldState"/>. Reads the PRIMARY (metadata never comes from a replica).</summary>
public sealed class MaterializedFieldStateRepository : TenantRepositoryBase, IMaterializedFieldState
{
    private const int InvalidColumnName = 207, InvalidObjectName = 208;
    internal static readonly TimeSpan ReadyCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IComputeSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MaterializedFieldStateRepository>? _logger;

    public MaterializedFieldStateRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IComputeSettings settings,
        IMemoryCache cache, ILogger<MaterializedFieldStateRepository>? logger = null)
        : base(connectionFactory, queryContext)
    {
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    private string ReadyKey => $"materialized_ready_{QueryContext.TenantId}";

    public async Task<IReadOnlySet<long>> ReadyFieldIdsAsync(CancellationToken ct = default)
    {
        if (!await _settings.IsComputeEnabledAsync(QueryContext.TenantId, ct)) return new HashSet<long>();
        if (_cache.TryGetValue(ReadyKey, out IReadOnlySet<long>? cached)) return cached!;

        IReadOnlySet<long> ready;
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            ready = (await connection.QueryAsync<long>(new CommandDefinition(
                "SELECT Id FROM meta.AppField WHERE MaterializeMode = @on AND MaterializeState = @ready AND IsDeleted = 0",
                new { on = MaterializeModes.On, ready = MaterializeStates.Ready }, cancellationToken: ct))).ToHashSet();
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            ready = new HashSet<long>();   // migration 097 not applied yet: nothing is stored
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cannot tell: read the dynamic way, which is always correct. Not cached, so the next read asks again.
            _logger?.LogWarning(ex, "Stored columns: could not read which fields are ready; reads use the dynamic path.");
            return new HashSet<long>();
        }
        _cache.Set(ReadyKey, ready, ReadyCacheTtl);
        return ready;
    }

    public async Task<bool> HasPendingWorkAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default)
    {
        if (tableIds.Count == 0) return false;
        var key = $"materialized_pending_{QueryContext.TenantId}_{string.Join(",", tableIds.Distinct().Order())}";
        if (_cache.TryGetValue(key, out bool cached)) return cached;
        bool pending;
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            // IX_ComputeOutbox_Table (TableId, Id): one seek per table.
            pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM meta.ComputeOutbox WHERE TableId IN @tableIds) THEN 1 ELSE 0 END",
                new { tableIds = tableIds.Distinct().ToList() }, cancellationToken: ct)) == 1;
        }
        catch (SqlException ex) when (ex.Number == InvalidObjectName)
        {
            pending = false;
        }
        _cache.Set(key, pending, PendingCacheTtl);
        return pending;
    }

    public async Task<StoredColumnStatus?> GetStatusAsync(AppField field, CancellationToken ct = default)
    {
        if (field.IsSystem || !PhysicalNaming.IsMaterializableTypeCode(field.TypeCode)) return null;
        var enabled = await _settings.IsComputeEnabledAsync(QueryContext.TenantId, ct);
        if (!enabled) return new StoredColumnStatus(StoredColumnStatuses.Disabled, null, false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            // Fresh from the table, not the field cache: the worker changes these columns.
            var row = await connection.QuerySingleOrDefaultAsync<(byte Mode, byte State, DateTime? BuiltOn)>(new CommandDefinition(
                "SELECT MaterializeMode, MaterializeState, MaterializedOn FROM meta.AppField WHERE Id = @id AND IsDeleted = 0",
                new { id = field.Id }, cancellationToken: ct));
            var status = StoredColumnStatuses.From(true, row.Mode, row.State, _settings.StoreAllCalculatedFields);
            var updating = status == StoredColumnStatuses.Ready && await HasPendingWorkAsync(MaterializedReadFields.InputTables(field).Distinct().ToList(), ct);
            return new StoredColumnStatus(status, status == StoredColumnStatuses.Ready ? row.BuiltOn : null, updating);
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            return new StoredColumnStatus(StoredColumnStatuses.Disabled, null, false);   // migration 097 not applied yet
        }
    }

    private const string CandidatesSql = """
        SELECT af.Id, af.AppTableId, af.Fid, ft.Code AS TypeCode, af.Settings
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.MaterializeMode = 1 AND af.MaterializeState IN (1, 2) AND af.IsDeleted = 0
        """;

    private const string RefusedSql = """
        SELECT af.Id, af.AppTableId, af.Fid, ft.Code AS TypeCode, af.Settings
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.MaterializeMode = 2 AND af.IsDeleted = 0
        """;

    public async Task InvalidateAsync(IReadOnlyCollection<long> fieldIds, IDbTransaction? transaction, CancellationToken ct = default)
    {
        if (fieldIds.Count == 0) return;
        SqlConnection? owned = null;
        try
        {
            var connection = transaction?.Connection ?? (owned = await OpenConnectionAsync(ct));
            var changed = (await connection.QueryAsync<(long Id, long AppTableId, int? Fid)>(new CommandDefinition(
                "SELECT Id, AppTableId, Fid FROM meta.AppField WHERE Id IN @fieldIds", new { fieldIds }, transaction, cancellationToken: ct))).AsList();
            if (changed.Count == 0) return;

            var changedKeys = changed.Where(c => c.Fid.HasValue).Select(c => new FieldKey(c.AppTableId, c.Fid!.Value)).ToHashSet();
            var candidates = (await connection.QueryAsync<AppField>(new CommandDefinition(CandidatesSql, transaction: transaction, cancellationToken: ct))).AsList();
            var stale = AffectedBy(candidates, changedKeys, fieldIds.ToHashSet());

            // A field the rules refused (Not allowed) may be storable after this change — a formula saved empty and filled in later, an
            // input that stopped being encrypted, a summary whose target formula became storable. It and the refused fields that read the
            // changed one are switched back on to be decided again by the next build (refused again at once if the rules still say no),
            // instead of waiting for the nightly re-check.
            var refused = (await connection.QueryAsync<AppField>(new CommandDefinition(RefusedSql, transaction: transaction, cancellationToken: ct))).AsList();
            var reopen = AffectedBy(refused, changedKeys, fieldIds.ToHashSet());
            if (stale.Count == 0 && reopen.Count == 0) return;

            if (stale.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE meta.AppField SET MaterializeState = 0, MaterializeVersion = MaterializeVersion + 1
                    WHERE Id IN @stale AND MaterializeMode = 1
                    """, new { stale }, transaction, cancellationToken: ct));
            if (reopen.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE meta.AppField SET MaterializeMode = 1, MaterializeState = 0, MaterializeVersion = MaterializeVersion + 1
                    WHERE Id IN @reopen AND MaterializeMode = 2
                    """, new { reopen }, transaction, cancellationToken: ct));
            _cache.Remove(ReadyKey);
            _logger?.LogInformation("Stored columns: {Stale} field(s) stop using their stored column until rebuilt and {Reopened} refused field(s) are decided again (a field they read changed).",
                stale.Count, reopen.Count);
        }
        catch (SqlException ex) when (ex.Number is InvalidColumnName or InvalidObjectName)
        {
            // Migration 097 not applied yet: nothing is stored, nothing to invalidate.
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    /// <summary>The switched-on fields that are one of <paramref name="changedIds"/> or read one of <paramref name="changedKeys"/>. A
    /// formula (reads not listable here) is invalidated by any change on its own table.</summary>
    internal static IReadOnlyList<long> AffectedBy(IReadOnlyList<AppField> candidates, IReadOnlySet<FieldKey> changedKeys, IReadOnlySet<long> changedIds) =>
        candidates.Where(c =>
                changedIds.Contains(c.Id)
                || (DependencyGraph.InputKeysOf(c) is { } inputs
                    ? inputs.Any(changedKeys.Contains)
                    : changedKeys.Any(k => k.TableId == c.AppTableId)))
            .Select(c => c.Id).Distinct().ToList();
}
