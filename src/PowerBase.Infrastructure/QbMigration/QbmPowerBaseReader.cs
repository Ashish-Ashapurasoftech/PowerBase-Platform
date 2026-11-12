using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Read-only access to the PowerBase metadata the migration needs (tables, fields, relationships, users).
/// Plain SQL against the same tables the repositories read, so the worker doesn't need the API's service graph.</summary>
public sealed class QbmPowerBaseReader(ITenantConnectionFactory tenant, Func<SqlConnection> control)
{
    public sealed record PbTable(long Id, long AppId, string Name, string? Alias, long? KeyFieldId);

    /// <summary>A Reference field on a child table and the parent field its value must match.</summary>
    public sealed record PbReference(long ChildTableId, int ReferenceFid, long ParentTableId, AppField ParentKey);

    public async Task<IReadOnlyList<PbTable>> TablesAsync(long appId, CancellationToken ct = default)
    {
        await using var conn = await tenant.CreateAsync(ct);
        return (await conn.QueryAsync<PbTable>(new CommandDefinition(
            "SELECT Id, AppId, Name, Alias, KeyFieldId FROM meta.AppTable WHERE AppId = @appId AND IsDeleted = 0 ORDER BY Id",
            new { appId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<AppField>> FieldsAsync(long tableId, CancellationToken ct = default)
    {
        await using var conn = await tenant.CreateAsync(ct);
        return (await conn.QueryAsync<AppField>(new CommandDefinition("""
            SELECT af.Id, af.PublicId, af.AppTableId, af.FieldTypeId, ft.Code AS TypeCode, af.Name, af.Label, af.Description,
                   af.PhysicalColumnName, af.IsSystem, af.IsUnique, af.IsEncrypted, af.IsHidden, af.IsSearchable, af.IsFilterable,
                   af.Fid, af.IsDeleted
            FROM meta.AppField af JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
            WHERE af.AppTableId = @tableId AND af.IsDeleted = 0
            """, new { tableId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Relationships of an app with the parent field the reference value names: the relationship's display key,
    /// else the parent table's key field, else Record ID#.</summary>
    public async Task<IReadOnlyList<PbReference>> ReferencesAsync(long appId, CancellationToken ct = default)
    {
        await using var conn = await tenant.CreateAsync(ct);
        var rows = await conn.QueryAsync<(long ChildTableId, int ReferenceFid, long ParentTableId, long? KeyFieldId)>(new CommandDefinition("""
            SELECT r.ChildTableId, r.ReferenceFid, r.ParentTableId, COALESCE(r.DisplayKeyFieldId, t.KeyFieldId) AS KeyFieldId
            FROM meta.Relationship r JOIN meta.AppTable t ON t.Id = r.ParentTableId
            WHERE r.AppId = @appId AND r.IsDeleted = 0
            """, new { appId }, cancellationToken: ct));
        var result = new List<PbReference>();
        foreach (var r in rows)
        {
            var parentFields = await FieldsAsync(r.ParentTableId, ct);
            var key = (r.KeyFieldId is { } k ? parentFields.FirstOrDefault(f => f.Id == k) : null)
                      ?? parentFields.First(f => f.Fid == 3);
            result.Add(new PbReference(r.ChildTableId, r.ReferenceFid, r.ParentTableId, key));
        }
        return result;
    }

    /// <summary>Parent record ids for key values, in batches. Only for a parent key that is stored as plain text or a number;
    /// an encrypted key can't be matched in SQL, so the caller blocks that relationship instead.</summary>
    public async Task<Dictionary<string, long>> ParentIdsByKeyAsync(long parentTableId, AppField key, IReadOnlyCollection<string> values, CancellationToken ct = default)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        if (values.Count == 0) return found;
        var column = PhysicalNaming.GetPhysicalColumnName(key);
        await using var conn = await tenant.CreateAsync(ct);
        foreach (var batch in values.Chunk(1000))
        {
            var hits = await conn.QueryAsync<(string Key, long Id)>(new CommandDefinition(
                $"SELECT CAST({column} AS NVARCHAR(400)) AS [Key], Id FROM {PhysicalNaming.FullTableName(parentTableId)} WHERE IsDeleted = 0 AND CAST({column} AS NVARCHAR(400)) IN @batch",
                new { batch }, cancellationToken: ct));
            foreach (var (k, id) in hits) found[k] = id;
        }
        return found;
    }

    /// <summary>PowerBase user ids for emails, limited to members of the tenant (active or not).</summary>
    public async Task<Dictionary<string, long>> UserIdsAsync(long tenantId, IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (emails.Count == 0) return found;
        await using var conn = control();
        foreach (var batch in emails.Select(e => e.Trim().ToUpperInvariant()).Distinct().Chunk(1000))
        {
            var hits = await conn.QueryAsync<(string Email, long Id)>(new CommandDefinition("""
                SELECT u.EmailNormalized, u.Id FROM core.[User] u
                JOIN meta.TenantUser tu ON tu.UserId = u.Id AND tu.TenantId = @tenantId AND tu.IsDeleted = 0
                WHERE u.IsDeleted = 0 AND u.EmailNormalized IN @batch
                """, new { tenantId, batch }, cancellationToken: ct));
            foreach (var (email, id) in hits) found[email] = id;
        }
        return found;
    }
}
