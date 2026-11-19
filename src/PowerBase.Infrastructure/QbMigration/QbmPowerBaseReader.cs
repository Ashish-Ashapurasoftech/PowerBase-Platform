using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Read-only access to the PowerBase metadata the migration needs (tables, fields, relationships, users).
/// Plain SQL against the same tables the repositories read, so the worker doesn't need the API's service graph.</summary>
public sealed class QbmPowerBaseReader(ITenantConnectionFactory tenant, Func<SqlConnection> control, IEncryptionService? encryption = null)
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

    /// <summary>Every non-deleted parent's key and Id, in one pass over the parent table (see <see cref="QbmParentKeyCache"/>).
    /// For a duplicated key the highest Id wins. In an encrypted app (or for an encrypted field) the key column holds ciphertext, so
    /// the keys are decrypted first; comparing ciphertext with a child's plain key matches nothing and the links would all be empty.
    /// The read stops, instead of returning a map that can't match, when the keys can't be decrypted.</summary>
    public async Task<IReadOnlyDictionary<string, long>> ParentKeyMapAsync(long parentTableId, AppField key, long tenantId, long appId, CancellationToken ct = default)
    {
        var column = PhysicalNaming.GetPhysicalColumnName(key);
        await using var conn = await tenant.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var appEncrypted = await conn.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT IsEncrypted FROM meta.App WHERE Id = @appId AND IsDeleted = 0", new { appId }, cancellationToken: ct)) ?? false;
        var rows = (await conn.QueryAsync<(string Key, long Id)>(new CommandDefinition(
            $"SELECT CAST({column} AS NVARCHAR(MAX)) AS [Key], Id FROM {PhysicalNaming.FullTableName(parentTableId)} WHERE IsDeleted = 0 AND {column} IS NOT NULL ORDER BY Id",
            commandTimeout: 900, cancellationToken: ct))).ToList();

        FieldEncryptionContext? decryptor = null;
        if (FieldEncryptionContext.StoresCiphertext(key, appEncrypted))
        {
            if (encryption is null)
                throw new InvalidOperationException($"Parent key '{key.Label ?? key.Name}' is encrypted but no encryption service is available; its links can't be matched.");
            decryptor = await FieldEncryptionContext.ResolveAsync(conn, appId, tenantId, encryption, null, ct);
            if (!decryptor.IsActive)
                throw new InvalidOperationException($"Parent key '{key.Label ?? key.Name}' is encrypted but the app has no active encryption key; its links can't be matched.");
        }

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        var decrypted = 0;
        foreach (var (k, id) in rows)
        {
            var text = k;
            if (decryptor is not null)
            {
                // DecryptValueAsync returns the input when it can't decrypt (legacy plaintext rows are tolerated), so a changed
                // value is the proof that decryption worked.
                text = await decryptor.DecryptValueAsync(k, ct);
                if (!ReferenceEquals(text, k) && text != k) decrypted++;
            }
            map[text] = id;
        }
        if (decryptor is not null && rows.Count > 0 && decrypted == 0)
            throw new InvalidOperationException($"The {rows.Count:N0} keys of '{key.Label ?? key.Name}' could not be decrypted (none changed); its links can't be matched.");
        return map;
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
