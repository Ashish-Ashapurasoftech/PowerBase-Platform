using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// What a Summary field's matching criteria may contain: the same rules as a report's filter
/// (known operators, at most <see cref="CommonReportValidationHelpers.MaxFilterTreeDepth"/> levels
/// of groups), over the child table's fields that have a column to filter on — so no calculated
/// (Formula/Summary) fields; a Lookup is filtered on the parent column it pulls down (see
/// <see cref="SummaryLookupSources"/>), except a range, whose end has no single value. Two report features have no meaning for a summary and are
/// refused: "is the current user" (a summary shows everyone the same value) and "&lt;ask the
/// user&gt;" (there is no one to ask). One thing only a summary has: comparing a child field to a
/// field on the child's own parent record ("parentField", see <see cref="ParentFieldScope"/>) —
/// again only fields with a column, so no calculated parent fields.
/// </summary>
public static class SummaryCriteriaValidator
{
    /// <summary>Throws <see cref="ValidationException"/> (keyed "matchingCriteria") when the
    /// criteria can't be used.</summary>
    public static void Validate(FilterGroup? criteria, IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        if (FindProblem(criteria, childFields, parentFields, lookupSources) is { } problem)
            throw new ValidationException(new Dictionary<string, string[]> { ["matchingCriteria"] = [problem] });
    }

    /// <summary>The first reason the criteria can't be used, or null when they can.</summary>
    public static string? FindProblem(FilterGroup? criteria, IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        if (criteria is null) return null;

        var fieldsByFid = ByFid(childFields);
        var parentFieldsByFid = ByFid(parentFields);
        var errors = new Dictionary<string, string[]>();
        CommonReportValidationHelpers.ValidateFilterGroup(criteria, fieldsByFid.Keys.ToHashSet(), errors,
            validParentFieldIds: parentFieldsByFid.Keys.ToHashSet());
        if (errors.Count > 0) return errors.Values.SelectMany(messages => messages).First();

        return FindUnsupported(criteria, fieldsByFid, parentFieldsByFid, lookupSources);
    }

    private static Dictionary<long, AppField> ByFid(IReadOnlyList<AppField> fields) =>
        fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

    private static string DisplayName(AppField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;

    private static string? FindUnsupported(FilterGroup group, IReadOnlyDictionary<long, AppField> fieldsByFid,
        IReadOnlyDictionary<long, AppField> parentFieldsByFid, IReadOnlyDictionary<long, AppField>? lookupSources)
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
                if (string.Equals(cond.Operator, "isCurrentUser", StringComparison.OrdinalIgnoreCase))
                    return "Matching criteria can't use \"is the current user\": a summary shows everyone the same value.";
                if (string.Equals(cond.ValueMode, "ask", StringComparison.OrdinalIgnoreCase))
                    return "Matching criteria can't ask the user for a value.";
                if (ParentFieldScope.IsParentFieldMode(cond.ValueMode)
                    && parentFieldsByFid[cond.ValueFieldId!.Value] is var parentField   // checked by ValidateFilterGroup too
                    && PhysicalNaming.IsComputedTypeCode(parentField.TypeCode))
                    return $"'{DisplayName(parentField)}' is calculated, so matching criteria can't compare to it.";
            }
            if (node.Group is { } sub && FindUnsupported(sub, fieldsByFid, parentFieldsByFid, lookupSources) is { } problem)
                return problem;
        }
        return null;
    }

    /// <summary>A stored or system field, or a lookup of one that isn't a range.</summary>
    private static bool IsFilterable(AppField field, IReadOnlyDictionary<long, AppField>? lookupSources) =>
        SummaryLookupSources.IsReadable(field, lookupSources)
        && !(SummaryLookupSources.IsLookup(field) && PhysicalNaming.IsRangeTypeCode(lookupSources![field.Fid!.Value].TypeCode));
}
