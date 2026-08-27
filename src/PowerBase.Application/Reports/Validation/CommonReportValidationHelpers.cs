using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Shared validation logic for all report types — used to be duplicated (and inconsistently so)
/// between CreateReportCommandHandler and UpdateReportCommandHandler; now called once from each
/// per-type <see cref="IReportConfigValidator"/>. Fixes a pre-existing gap: Update never validated
/// column/filter field IDs against the table the way Create did — every caller now gets the same
/// checks regardless of Create vs Update.
/// </summary>
public static class CommonReportValidationHelpers
{
    public static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "contains", "notContains", "startsWith", "notStartsWith",
        "gt", "gte", "lt", "lte", "in", "notIn", "isEmpty", "isNotEmpty", "date_eq",
    };

    /// <summary>Base aggregate functions valid everywhere; Summary/Chart validators layer the
    /// field-type-conditional Summarize-By restriction (see FieldTypeCategoryHelper) on top of this.</summary>
    public static readonly HashSet<string> AllowedAggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "Count", "Sum", "Avg", "Min", "Max", "DistinctCount", "StdDev", "Median" };

    /// <summary>Hard server-side cap on nested filter-group depth, matching the "3-level nested
    /// ALL/ANY" UI limit — enforced here too so a client bypassing the UI can't exceed it.</summary>
    public const int MaxFilterTreeDepth = 3;

    public static HashSet<long> GetValidFieldIds(IReadOnlyList<AppField> tableFields) =>
        tableFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();

    public static void AddError(IDictionary<string, string[]> errors, string field, string message)
    {
        if (errors.TryGetValue(field, out var existing))
            errors[field] = [.. existing, message];
        else
            errors[field] = [message];
    }

    public static void ValidateColumns(List<long> columns, HashSet<long> validFieldIds, IDictionary<string, string[]> errors)
    {
        if (columns.Count == 0)
            return;

        var invalid = columns.Where(id => !validFieldIds.Contains(id)).ToList();
        if (invalid.Count > 0)
            AddError(errors, "columns", $"Unknown field IDs: {string.Join(", ", invalid)}");
    }

    public static void ValidateFilterGroup(FilterGroup? group, HashSet<long> validFieldIds, IDictionary<string, string[]> errors, int depth = 1)
    {
        if (group is null)
            return;

        if (depth > MaxFilterTreeDepth)
        {
            AddError(errors, "filterTree", $"Filter groups may be nested at most {MaxFilterTreeDepth} levels deep.");
            return;
        }

        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond)
            {
                if (!AllowedOperators.Contains(cond.Operator))
                    AddError(errors, "filterTree", $"Invalid operator '{cond.Operator}'. Allowed: {string.Join(", ", AllowedOperators)}");
                if (!validFieldIds.Contains(cond.FieldId))
                    AddError(errors, "filterTree", $"Unknown field ID in filter: {cond.FieldId}");
            }

            if (node.Group is { } sub)
                ValidateFilterGroup(sub, validFieldIds, errors, depth + 1);
        }
    }

    public static void ValidateAggregations(List<SummaryAggregationCommand> aggregations, HashSet<long> validFieldIds, IDictionary<string, string[]> errors)
    {
        foreach (var agg in aggregations)
        {
            if (!AllowedAggregateFunctions.Contains(agg.Function))
                AddError(errors, "aggregations", $"Invalid function '{agg.Function}'. Allowed: {string.Join(", ", AllowedAggregateFunctions)}");
            if (!validFieldIds.Contains(agg.FieldId))
                AddError(errors, "aggregations", $"Unknown field ID in aggregation: {agg.FieldId}");
        }
    }

    /// <summary>Reports the given field as an error when it's populated with a non-default value
    /// — used so each per-type validator can reject config that belongs to a different report
    /// type (e.g. Aggregations sent on a Table report) instead of silently accepting and storing
    /// it, per the "each report type must only allow settings applicable to that type" requirement.</summary>
    public static void ForbidIfPopulated(bool isPopulated, string field, string reportTypeLabel, IDictionary<string, string[]> errors)
    {
        if (isPopulated)
            AddError(errors, field, $"'{field}' is not applicable to {reportTypeLabel} reports and must be left empty.");
    }

    public static void RequirePopulated(bool isPopulated, string field, string reportTypeLabel, IDictionary<string, string[]> errors)
    {
        if (!isPopulated)
            AddError(errors, field, $"'{field}' is required for {reportTypeLabel} reports.");
    }

    public static void ValidateDynamicFilterFields(ReportConfigValidationInput input, HashSet<long> validFieldIds, IDictionary<string, string[]> errors)
    {
        if (!string.Equals(input.DynamicFilterType, "Custom", StringComparison.OrdinalIgnoreCase))
            return;

        var invalidLegacy = input.CustomDynamicFilterFields.Where(id => !validFieldIds.Contains(id)).ToList();
        if (invalidLegacy.Count > 0)
            AddError(errors, "customDynamicFilterFields", $"Unknown field IDs: {string.Join(", ", invalidLegacy)}");

        var invalidItems = (input.CustomDynamicFilterItems ?? [])
            .Where(i => !validFieldIds.Contains(i.FieldId))
            .Select(i => i.FieldId)
            .ToList();
        if (invalidItems.Count > 0)
            AddError(errors, "customDynamicFilterItems", $"Unknown field IDs: {string.Join(", ", invalidItems)}");
    }
}
