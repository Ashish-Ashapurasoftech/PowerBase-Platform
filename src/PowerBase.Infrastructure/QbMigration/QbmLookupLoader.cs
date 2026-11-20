using System.Text.Json;
using PowerBase.Application.QbMigration;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Loads, for one chunk of QuickBase rows, the PowerBase user ids and parent record ids the transformer needs.
/// Used by the loader and by validation, so both resolve values the same way.</summary>
public sealed class QbmLookupLoader(QbmPowerBaseReader pb, QbUserParentIndexes? userParents = null, QbmParentKeyCache? parentKeys = null,
    QbKeyIndexes? selfKeys = null)
{
    /// <summary>Users and parent keys the chunk refers to, fetched in one go per chunk.</summary>
    public async Task<IQbValueLookups> LoadAsync(QbmTableContext ctx, IReadOnlyList<QbRow> rows, CancellationToken ct)
    {
        var userFids = ctx.Mappings.Where(m => m.Action == QbFieldAction.System && m.Qb.Id is QbSystemFids.RecordOwner or QbSystemFids.LastModifiedBy
                                               || m.Action == QbFieldAction.Load && m.PbTypeCode is "User" or "MultiUser")
            .Select(m => m.Qb.Id).ToHashSet();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            foreach (var fid in userFids)
                if (row.Values.TryGetValue(fid, out var v))
                    foreach (var email in Emails(v)) emails.Add(email);
        var users = await pb.UserIdsAsync(ctx.TenantId, emails, ct);

        var references = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        foreach (var (pbFid, rel) in ctx.ReferencesByKey)
        {
            // A table pointing at itself by key: its parent may not be loaded yet, so the keys come from QuickBase (see QbKeyIndex).
            if (selfKeys is not null && rel.ParentTableId == ctx.PbTable.Id)
            {
                var keyMapping = ctx.Mappings.FirstOrDefault(m => m.Action == QbFieldAction.Load && m.PbFid == rel.ParentKey.Fid)
                    ?? throw new InvalidOperationException($"'{ctx.PbTable.Name}' points at itself by key '{rel.ParentKey.Label ?? rel.ParentKey.Name}', which is not a loaded field; its links can't be matched.");
                references[pbFid] = await selfKeys.GetAsync(new QbKeyIndexSpec(ctx.QbTableId, keyMapping.Qb.Id));
                continue;
            }
            // The parent's keys are read once for the process; asking per chunk scans the parent every time (see QbmParentKeyCache).
            if (parentKeys is not null)
            {
                references[pbFid] = await parentKeys.GetAsync(rel.ParentTableId, rel.ParentKey, ctx.TenantId, ctx.PbTable.AppId, ct);
                continue;
            }
            var qbFid = ctx.Mappings.First(m => m.Action == QbFieldAction.Load && m.PbFid == pbFid).Qb.Id;
            var keys = rows.Select(r => r.Values.TryGetValue(qbFid, out var v) ? KeyText(v) : null)
                .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct().ToList();
            references[pbFid] = await pb.ParentIdsByKeyAsync(rel.ParentTableId, rel.ParentKey, keys, ct);
        }

        // User-keyed references: the parent table's user field, read from QuickBase (cached for the process).
        var parents = new Dictionary<int, QbUserParentIndex>();
        if (ctx.UserReferences is { Count: > 0 } specs)
        {
            if (userParents is null) throw new InvalidOperationException("The table has user-keyed references but no parent index was provided.");
            foreach (var (pbFid, spec) in specs) parents[pbFid] = await userParents.GetAsync(spec);
        }
        return new Lookups(users, references, parents);
    }

    private static IEnumerable<string> Emails(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object when v.TryGetProperty("email", out var e) && e.GetString() is { Length: > 0 } s => [s],
        JsonValueKind.Array => v.EnumerateArray().SelectMany(Emails),
        _ => [],
    };

    private static string? KeyText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    private sealed class Lookups(Dictionary<string, long> users, Dictionary<int, IReadOnlyDictionary<string, long>> references,
        Dictionary<int, QbUserParentIndex> userParents) : IQbValueLookups
    {
        public long? UserParentId(int pbFid, string? qbUserId, string? email) =>
            userParents.TryGetValue(pbFid, out var index) ? index.Find(qbUserId, email) : null;

        public long? UserId(string email) => users.TryGetValue(email.Trim().ToUpperInvariant(), out var id) ? id : null;
        public long? ReferenceId(int pbFid, string key) =>
            references.TryGetValue(pbFid, out var map) && map.TryGetValue(key, out var id) ? id : null;
    }
}
