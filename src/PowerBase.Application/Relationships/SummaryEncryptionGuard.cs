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

    /// <summary>Why a summary reading these child columns can't be supported, or null when none of
    /// them is encrypted. The reference column is always read, so in an encrypted app every
    /// summary is affected.</summary>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria)
    {
        if (app.IsEncrypted)
            return "Summary fields aren't available in an encrypted app yet — encrypted values can't be summarized.";

        var fieldByFid = childFields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var readFids = new List<long>();
        if (targetFid.HasValue) readFids.Add(targetFid.Value);
        if (sortFid.HasValue) readFids.Add(sortFid.Value);
        readFids.AddRange(ConditionFieldIds(matchingCriteria));

        var encrypted = readFids.Distinct()
            .Select(fid => fieldByFid.GetValueOrDefault(fid))
            .Where(f => IsEncrypted(app, f))
            .Select(f => string.IsNullOrWhiteSpace(f!.Label) ? f.Name : f.Label)
            .ToList();
        return encrypted.Count == 0
            ? null
            : $"Summary fields can't use encrypted fields yet: {string.Join(", ", encrypted.Select(l => $"'{l}'"))}.";
    }

    /// <summary>Same check for a summary that also reads an existing reference field (e.g. a
    /// relationship's reference) — needed when that field itself may be marked encrypted.</summary>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields, int referenceFid,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria)
    {
        var reference = childFields.FirstOrDefault(f => f.Fid == referenceFid);
        if (!app.IsEncrypted && IsEncrypted(app, reference))
            return $"Summary fields can't use this relationship: its reference field '{(string.IsNullOrWhiteSpace(reference!.Label) ? reference.Name : reference.Label)}' is encrypted.";
        return FindProblem(app, childFields, targetFid, sortFid, matchingCriteria);
    }

    private static IEnumerable<long> ConditionFieldIds(FilterGroup? group)
    {
        if (group is null) yield break;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c) yield return c.FieldId;
            foreach (var id in ConditionFieldIds(node.Group)) yield return id;
        }
    }
}
