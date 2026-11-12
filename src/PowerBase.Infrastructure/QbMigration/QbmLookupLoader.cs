using System.Text.Json;
using PowerBase.Application.QbMigration;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Loads, for one chunk of QuickBase rows, the PowerBase user ids and parent record ids the transformer needs.
/// Used by the loader and by validation, so both resolve values the same way.</summary>
public sealed class QbmLookupLoader(QbmPowerBaseReader pb)
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

        var references = new Dictionary<int, Dictionary<string, long>>();
        foreach (var (pbFid, rel) in ctx.ReferencesByKey)
        {
            var qbFid = ctx.Mappings.First(m => m.Action == QbFieldAction.Load && m.PbFid == pbFid).Qb.Id;
            var keys = rows.Select(r => r.Values.TryGetValue(qbFid, out var v) ? KeyText(v) : null)
                .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct().ToList();
            references[pbFid] = await pb.ParentIdsByKeyAsync(rel.ParentTableId, rel.ParentKey, keys, ct);
        }
        return new Lookups(users, references);
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

    private sealed class Lookups(Dictionary<string, long> users, Dictionary<int, Dictionary<string, long>> references) : IQbValueLookups
    {
        public long? UserId(string email) => users.TryGetValue(email.Trim().ToUpperInvariant(), out var id) ? id : null;
        public long? ReferenceId(int pbFid, string key) =>
            references.TryGetValue(pbFid, out var map) && map.TryGetValue(key, out var id) ? id : null;
    }
}
