using System.Text.Json;
using PowerBase.Application.Reports;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>Every field a saved report definition points at: columns, sort/group levels, filters
/// (including a "compare to another field" value), aggregations, dynamic filters and the chart's
/// series/gauge fields. Report definitions identify fields by <c>Fid</c>.</summary>
public static class ReportReferenceExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void Extract(string? definitionJson, TableFieldIndex table, FieldReferenceCollector collector)
    {
        var definition = Parse(definitionJson);
        if (definition is null) return;

        void Add(long? fid, string usage) => collector.Add(table.IdOfFid(fid), usage);

        // Columns — empty means "all reportable fields", which isn't an explicit reference.
        foreach (var fid in definition.Columns) Add(fid, FieldReferenceUsages.Column);

        // Sort / group: the unified Table list, the legacy single-field forms, and Summary Rows.
        foreach (var level in definition.TableSortGroup)
        {
            Add(level.FieldId, FieldReferenceUsages.Sort);
            if (level.IsGroup) Add(level.FieldId, FieldReferenceUsages.GroupBy);
        }
        foreach (var sort in definition.SortFields) Add(sort.FieldId, FieldReferenceUsages.Sort);
        Add(definition.SortFieldId, FieldReferenceUsages.Sort);
        Add(definition.GroupByFieldId, FieldReferenceUsages.GroupBy);
        foreach (var level in definition.RowGroupLevels) Add(level.FieldId, FieldReferenceUsages.GroupBy);
        foreach (var sort in definition.SummarySortFields) Add(sort.AggregationFieldId, FieldReferenceUsages.Sort);

        // Filters: the tree (supersedes the legacy flat list, but old reports may still carry either).
        if (definition.FilterTree is { } tree) ExtractFilterGroup(tree, table, collector);
        foreach (var filter in definition.Filters) Add(filter.FieldId, FieldReferenceUsages.Filter);

        foreach (var aggregation in definition.Aggregations) Add(aggregation.FieldId, FieldReferenceUsages.Aggregation);

        foreach (var fid in definition.CustomDynamicFilterFields) Add(fid, FieldReferenceUsages.DynamicFilter);
        foreach (var item in definition.CustomDynamicFilterItems) Add(item.FieldId, FieldReferenceUsages.DynamicFilter);

        if (definition.Chart is { } chart)
        {
            Add(chart.SeriesFieldId, FieldReferenceUsages.Chart);
            Add(chart.GaugeFieldId, FieldReferenceUsages.Chart);
            Add(chart.GaugeGoalFieldId, FieldReferenceUsages.Chart);
            foreach (var fid in chart.SecondaryAxisAggregationFieldIds) Add(fid, FieldReferenceUsages.Chart);
        }
    }

    /// <summary>Walks a filter tree, recording each condition's field (and, when it compares
    /// against another field, that field too). Shared with Summary-field child filters.</summary>
    public static void ExtractFilterGroup(FilterGroup group, TableFieldIndex table, FieldReferenceCollector collector, string usage = FieldReferenceUsages.Filter)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } condition)
            {
                collector.Add(table.IdOfFid(condition.FieldId), usage);
                if (string.Equals(condition.ValueMode, "field", StringComparison.OrdinalIgnoreCase))
                    collector.Add(table.IdOfFid(condition.ValueFieldId), FieldReferenceUsages.FilterValue);
            }
            if (node.Group is { } nested) ExtractFilterGroup(nested, table, collector, usage);
        }
    }

    private static ReportDefinition? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReportDefinition>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
