using System.Text.Json;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// What a Summary field's matching criteria may contain: the same rules as a report's filter
/// (known operators; groups may nest to any depth, unlike a report's
/// <see cref="CommonReportValidationHelpers.MaxFilterTreeDepth"/> levels), over the child table's fields that have a column to filter on — so no calculated
/// (Formula/Summary) fields; a Lookup is filtered on the parent column it pulls down (see
/// <see cref="SummaryLookupSources"/>), except a range, whose end has no single value. Two report features have no meaning for a summary and are
/// refused: "&lt;ask the user&gt;" (there is no one to ask). "is the current user" is allowed and
/// resolves to whoever is reading the summary. One thing only a summary has: comparing a child field to a
/// field on the child's own parent record ("parentField", see <see cref="ParentFieldScope"/>) — a stored field,
/// or a Lookup / Summary / Formula with a comparable value (<see cref="ParentFieldScope.IsComparable"/>), but never
/// the summary itself or a parent summary whose own criteria compare to it.
/// </summary>
public static class SummaryCriteriaValidator
{
    private static readonly JsonSerializerOptions FilterJsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Throws <see cref="ValidationException"/> (keyed "matchingCriteria") when the
    /// criteria can't be used.</summary>
    /// <param name="summaryFid">The summary being edited (null while it's being created) — its criteria can't compare to it.</param>
    public static void Validate(FilterGroup? criteria, IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<long, AppField>? lookupSources = null, long? summaryFid = null)
    {
        if (FindProblem(criteria, childFields, parentFields, lookupSources, summaryFid) is { } problem)
            throw new ValidationException(new Dictionary<string, string[]> { ["matchingCriteria"] = [problem] });
    }

    /// <summary>The first reason the criteria can't be used, or null when they can.</summary>
    public static string? FindProblem(FilterGroup? criteria, IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<long, AppField>? lookupSources = null, long? summaryFid = null)
    {
        if (criteria is null) return null;

        var fieldsByFid = ByFid(childFields);
        var parentFieldsByFid = ByFid(parentFields);
        var errors = new Dictionary<string, string[]>();
        CommonReportValidationHelpers.ValidateFilterGroup(criteria, fieldsByFid.Keys.ToHashSet(), errors,
            validParentFieldIds: parentFieldsByFid.Keys.ToHashSet(), unlimitedDepth: true);
        if (errors.Count > 0) return errors.Values.SelectMany(messages => messages).First();

        return FindUnsupported(criteria, fieldsByFid, parentFieldsByFid, lookupSources, summaryFid);
    }

    private static Dictionary<long, AppField> ByFid(IReadOnlyList<AppField> fields) =>
        fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

    private static string DisplayName(AppField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    private static string? FindUnsupported(FilterGroup group, IReadOnlyDictionary<long, AppField> fieldsByFid,
        IReadOnlyDictionary<long, AppField> parentFieldsByFid, IReadOnlyDictionary<long, AppField>? lookupSources, long? summaryFid)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond)
            {
                var field = fieldsByFid[cond.FieldId];   // ValidateFilterGroup has checked it exists
                if (!IsFilterable(field, lookupSources))
                    return $"'{DisplayName(field)}' is calculated, so it can't be used in matching criteria.";
                if (string.Equals(cond.ValueMode, "field", StringComparison.OrdinalIgnoreCase)
                    && cond.ValueFieldId is long otherFid && fieldsByFid.TryGetValue(otherFid, out var other)
                    && !IsFilterable(other, lookupSources))
                    return $"'{DisplayName(other)}' is calculated, so matching criteria can't compare to it.";
                if (string.Equals(cond.ValueMode, "ask", StringComparison.OrdinalIgnoreCase))
                    return "Matching criteria can't ask the user for a value.";
                if (ParentFieldScope.IsParentFieldMode(cond.ValueMode)
                    && parentFieldsByFid[cond.ValueFieldId!.Value] is var parentField)   // checked by ValidateFilterGroup too
                {
                    if (!ParentFieldScope.IsComparable(parentField))
                        return $"'{DisplayName(parentField)}' is calculated, so matching criteria can't compare to it.";
                    if (summaryFid is long self && DependsOnSummary(parentField, self, parentFieldsByFid, []))
                        return $"'{DisplayName(parentField)}' depends on this summary, so its matching criteria can't compare to it.";
                }
            }
            if (node.Group is { } sub && FindUnsupported(sub, fieldsByFid, parentFieldsByFid, lookupSources, summaryFid) is { } problem)
                return problem;
        }
        return null;
    }

    /// <summary>True when <paramref name="parentField"/> is the summary <paramref name="summaryFid"/>, or a parent
    /// summary whose own criteria compare (directly or through other summaries) to it — a value that can only be worked
    /// out after this summary's.</summary>
    private static bool DependsOnSummary(AppField parentField, long summaryFid,
        IReadOnlyDictionary<long, AppField> parentFieldsByFid, HashSet<long> seen)
    {
        if (parentField.Fid is not int fid) return false;
        if (fid == summaryFid) return true;
        if (parentField.TypeCode != nameof(Domain.Enums.FieldTypeCode.Summary) || !seen.Add(fid)) return false;
        var filterTree = FormulaTypeMap.ParseSummarySettings(parentField.Settings)?.FilterTree;
        if (string.IsNullOrWhiteSpace(filterTree)) return false;
        FilterGroup? criteria;
        try { criteria = JsonSerializer.Deserialize<FilterGroup>(filterTree, FilterJsonOpts); }
        catch (JsonException) { return false; }
        return SummaryEncryptionGuard.ParentConditionFieldIds(criteria)
            .Any(next => parentFieldsByFid.TryGetValue(next, out var other) && DependsOnSummary(other, summaryFid, parentFieldsByFid, seen));
    }

    /// <summary>A stored or system field, a lookup of one that isn't a range, or a Formula/Summary — or a lookup of one
    /// (computed per record and judged in memory).</summary>
    private static bool IsFilterable(AppField field, IReadOnlyDictionary<long, AppField>? lookupSources) =>
        (SummaryLookupSources.IsReadable(field, lookupSources) || SummaryComputedTargets.ResultKind(field) is not null
            || SummaryLookupSources.ComputedValueKind(field, lookupSources) is not null)
        && !(SummaryLookupSources.IsLookup(field) && PhysicalNaming.IsRangeTypeCode(lookupSources![field.Fid!.Value].TypeCode));
}
