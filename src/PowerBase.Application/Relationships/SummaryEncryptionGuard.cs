using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Summaries over encrypted data. An encrypted column holds ciphertext, so SQL can't aggregate,
/// match or sort it (and in an encrypted app the reference column is ciphertext too). A summary
/// that reads any encrypted column is therefore computed in memory instead
/// (<see cref="EncryptedSummaryAggregator"/>): the child rows are decrypted and filtered, grouped
/// and aggregated here. That path covers the child table's own stored fields; what it can't do is
/// refused at creation (and shown blank when read) by FindProblem.
///
/// "Encrypted" follows the encryption context's own rule (FieldEncryptionContext): every
/// non-system field of an encrypted app, plus any non-system field marked IsEncrypted.
/// </summary>
public static class SummaryEncryptionGuard
{
    /// <summary>Operators the in-memory filter evaluator understands (isCurrentUser and the date
    /// ranges are rewritten to plain comparisons before it runs).</summary>
    private static readonly HashSet<string> InMemoryOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "contains", "notContains", "startsWith", "notStartsWith",
        "gt", "gte", "lt", "lte", "in", "notIn", "isEmpty", "isNotEmpty",
        "wildcard", "notWildcard", "includes", "notIncludes",
        "isCurrentUser", "during", "notDuring", "date_eq",
    };

    public static bool IsEncrypted(App app, AppField? field) =>
        field is not null && !field.IsSystem && (app.IsEncrypted || field.IsEncrypted);

    /// <summary>True when the summary reads an encrypted column — its reference column, target,
    /// Combined Text sort or a matching-criteria field — and so must be computed in memory.</summary>
    public static bool ReadsEncrypted(
        App app, IReadOnlyList<AppField> childFields, int? referenceFid,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields = null)
    {
        if (app.IsEncrypted) return true;
        if (referenceFid.HasValue && IsEncrypted(app, childFields.FirstOrDefault(f => f.Fid == referenceFid))) return true;
        var readFids = new List<long>();
        if (targetFid.HasValue) readFids.Add(targetFid.Value);
        if (sortFid.HasValue) readFids.Add(sortFid.Value);
        readFids.AddRange(ConditionFieldIds(matchingCriteria));
        return EncryptedNames(app, childFields, readFids).Any()
            || EncryptedNames(app, parentFields ?? [], ParentConditionFieldIds(matchingCriteria)).Any();
    }

    /// <summary>True when the matching criteria or the Combined Text sort read a Formula/Summary field of the child table:
    /// it has no column, so the summary is computed in memory (the formula is evaluated on each child record).</summary>
    public static bool ReadsComputed(IReadOnlyList<AppField> childFields, int? sortFid, FilterGroup? matchingCriteria) =>
        ConditionFieldIds(matchingCriteria).Concat(sortFid.HasValue ? [sortFid.Value] : [])
            .Distinct()
            .Any(fid => childFields.FirstOrDefault(f => f.Fid == fid) is { } f && SummaryComputedTargets.ResultKind(f) is not null);

    /// <summary>Why a summary reading encrypted data can't be computed, or null when it can (or
    /// reads no encrypted data at all, which SQL handles).</summary>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields = null,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
        => FindProblemCore(app, childFields, null, targetFid, sortFid, matchingCriteria, parentFields, lookupSources);

    /// <summary>Same check for a summary that also reads an existing reference field (e.g. a
    /// relationship's reference), which may itself be marked encrypted.</summary>
    public static string? FindProblem(
        App app, IReadOnlyList<AppField> childFields, int referenceFid,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields = null,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
        => FindProblemCore(app, childFields, referenceFid, targetFid, sortFid, matchingCriteria, parentFields, lookupSources);

    private static string? FindProblemCore(
        App app, IReadOnlyList<AppField> childFields, int? referenceFid,
        int? targetFid, int? sortFid, FilterGroup? matchingCriteria,
        IReadOnlyList<AppField>? parentFields, IReadOnlyDictionary<long, AppField>? lookupSources)
    {
        var readFids = new List<long>();
        if (targetFid.HasValue) readFids.Add(targetFid.Value);
        if (sortFid.HasValue) readFids.Add(sortFid.Value);
        readFids.AddRange(ConditionFieldIds(matchingCriteria));

        // A looked-up or compared-to parent field that is encrypted also pushes the summary onto
        // the in-memory path, which can't read other tables.
        var parentEncrypted = EncryptedNames(app, parentFields ?? [], ParentConditionFieldIds(matchingCriteria)).Any();
        var lookupEncrypted = EncryptedNames(app, childFields, readFids, lookupSources)
            .Except(EncryptedNames(app, childFields, readFids)).Any();
        var usesEncrypted = ReadsEncrypted(app, childFields, referenceFid, targetFid, sortFid, matchingCriteria)
            || parentEncrypted || lookupEncrypted;
        if (!usesEncrypted) return null;

        var byFid = childFields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        string Label(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label;

        // A lookup is read through its source field on the parent table (decrypted in memory too); it
        // can't be when that source is gone or calculated. Without the sources there is nothing to judge.
        var lookups = lookupSources is null ? [] : readFids.Distinct()
            .Select(fid => byFid.GetValueOrDefault(fid))
            .Where(f => f is not null && SummaryLookupSources.IsLookup(f!) && !SummaryLookupSources.IsReadable(f!, lookupSources))
            .Select(f => $"'{Label(f!)}'").ToList();
        if (lookups.Count > 0)
            return $"Summaries of encrypted data can't use lookup fields whose source field is missing or calculated: {string.Join(", ", lookups)}.";

        var badOperator = Operators(matchingCriteria).FirstOrDefault(o => !InMemoryOperators.Contains(o));
        if (badOperator is not null)
            return $"Summaries of encrypted data don't support the '{badOperator}' matching operator yet.";

        return null;
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

    private static IEnumerable<string> Operators(FilterGroup? group)
    {
        if (group is null) yield break;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c) yield return c.Operator;
            foreach (var op in Operators(node.Group)) yield return op;
        }
    }

    /// <summary>The child fields the criteria read: each condition's field, plus the other field
    /// of a "the value in the field" comparison.</summary>
    internal static IEnumerable<long> ConditionFieldIds(FilterGroup? group)
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
    internal static IEnumerable<long> ParentConditionFieldIds(FilterGroup? group)
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
