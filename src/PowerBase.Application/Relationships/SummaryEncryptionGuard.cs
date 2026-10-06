using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Summary fields don't support encrypted data yet. An encrypted column holds ciphertext, so SQL
/// can't aggregate, match or sort it: Count finds no children (the reference column is encrypted
/// too), Sum/Avg fail, and Combined Text would put ciphertext on screen. Until summaries decrypt
/// in memory, a summary that reads any encrypted column is refused at creation and shown blank
/// when read — its values are never computed, so ciphertext can never be displayed.
///
/// "Encrypted" follows the encryption context's own rule (FieldEncryptionContext): every
/// non-system field of an encrypted app, plus any non-system field marked IsEncrypted.
/// </summary>
public static class SummaryEncryptionGuard
{
    public static bool IsEncrypted(App app, AppField? field) =>
        field is not null && !field.IsSystem && (app.IsEncrypted || field.IsEncrypted);

    /// <summary>Why a summary reading these child columns — and the parent columns its matching
    /// criteria compare to — can't be supported, or null when none of them is encrypted. The
    /// reference column is always read, so in an encrypted app every summary is affected.</summary>
    /// <param name="lookupSources">The parent field each child lookup pulls down — a lookup the
    /// summary reads is encrypted when that field is (the SQL reads the parent's column).</param>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields = null,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        if (app.IsEncrypted)
            return "Summary fields aren't available in an encrypted app yet — encrypted values can't be summarized.";

        var readFids = new List<long>();
        if (targetFid.HasValue) readFids.Add(targetFid.Value);
        if (sortFid.HasValue) readFids.Add(sortFid.Value);
        readFids.AddRange(ConditionFieldIds(matchingCriteria));

        var encrypted = EncryptedNames(app, childFields, readFids, lookupSources)
            .Concat(EncryptedNames(app, parentFields ?? [], ParentConditionFieldIds(matchingCriteria)))
            .ToList();
        return encrypted.Count == 0
            ? null
            : $"Summary fields can't use encrypted fields yet: {string.Join(", ", encrypted.Select(l => $"'{l}'"))}.";
    }

    /// <summary>Same check for a summary that also reads an existing reference field (e.g. a
    /// relationship's reference) — needed when that field itself may be marked encrypted.</summary>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields, int referenceFid,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields = null,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        var reference = childFields.FirstOrDefault(f => f.Fid == referenceFid);
        if (!app.IsEncrypted && IsEncrypted(app, reference))
            return $"Summary fields can't use this relationship: its reference field '{(string.IsNullOrWhiteSpace(reference!.Label) ? reference.Name : reference.Label)}' is encrypted.";
        return FindProblem(app, childFields, targetFid, sortFid, matchingCriteria, parentFields, lookupSources);
    }

    private static IEnumerable<string> EncryptedNames(App app, IReadOnlyList<AppField> fields, IEnumerable<long> readFids,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        var fieldByFid = fields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        return readFids.Distinct()
            .Select(fid => fieldByFid.GetValueOrDefault(fid))
            .Where(f => IsEncrypted(app, f)
                || (f is not null && SummaryLookupSources.IsLookup(f) && lookupSources is not null
                    && lookupSources.TryGetValue(f.Fid!.Value, out var source) && IsEncrypted(app, source)))
            .Select(f => string.IsNullOrWhiteSpace(f!.Label) ? f.Name : f.Label);
    }

    /// <summary>The child fields the criteria read: each condition's field, plus the other field
    /// of a "the value in the field" comparison.</summary>
    private static IEnumerable<long> ConditionFieldIds(FilterGroup? group)
    {
        if (group is null) yield break;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                yield return c.FieldId;
                if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId is long other)
                    yield return other;
            }
            foreach (var id in ConditionFieldIds(node.Group)) yield return id;
        }
    }

    /// <summary>The parent fields the criteria compare to ("the value in the parent's field").</summary>
    private static IEnumerable<long> ParentConditionFieldIds(FilterGroup? group)
    {
        if (group is null) yield break;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c && ParentFieldScope.IsParentFieldMode(c.ValueMode) && c.ValueFieldId is long parentFid)
                yield return parentFid;
            foreach (var id in ParentConditionFieldIds(node.Group)) yield return id;
        }
    }
}
