using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.UpdateReport;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Type-agnostic snapshot of a report's incoming configuration, built from either
/// <see cref="CreateReportCommand"/> or <see cref="UpdateReportCommand"/> so both handlers can
/// run the exact same <see cref="IReportConfigValidator"/> logic instead of each re-implementing
/// their own (partial, drifting) checks. See <see cref="ReportConfigValidatorRegistry"/>.
/// </summary>
public sealed class ReportConfigValidationInput
{
    public required List<long> Columns { get; init; }
    public required List<SortSpec> SortFields { get; init; }
    public FilterGroup? FilterTree { get; init; }
    public long? GroupByFieldId { get; init; }
    public string? GroupByMode { get; init; }
    public bool HideTotals { get; init; }
    public bool? GroupDefaultCollapsed { get; init; }
    public bool GroupByDescending { get; init; }
    public required List<SummaryAggregationCommand> Aggregations { get; init; }
    public string? DynamicFilterType { get; init; }
    public required List<long> CustomDynamicFilterFields { get; init; }
    public List<CustomDynamicFilterItem>? CustomDynamicFilterItems { get; init; }
    public ChartConfigCommand? Chart { get; init; }
    public string ColumnsMode { get; init; } = "Custom";
    public List<SortGroupLevelCommand> TableSortGroup { get; init; } = [];
    public ReportOptionsCommand? Options { get; init; }

    public static ReportConfigValidationInput FromCreate(CreateReportCommand command) => new()
    {
        Columns = command.Columns,
        SortFields = command.SortFields,
        FilterTree = command.FilterTree,
        GroupByFieldId = command.GroupByFieldId,
        GroupByMode = command.GroupByMode,
        HideTotals = command.HideTotals,
        GroupDefaultCollapsed = command.GroupDefaultCollapsed,
        GroupByDescending = command.GroupByDescending,
        Aggregations = command.Aggregations,
        DynamicFilterType = command.DynamicFilterType,
        CustomDynamicFilterFields = command.CustomDynamicFilterFields,
        CustomDynamicFilterItems = command.CustomDynamicFilterItems,
        Chart = command.Chart,
        ColumnsMode = command.ColumnsMode,
        TableSortGroup = command.TableSortGroup ?? [],
        Options = command.Options,
    };

    public static ReportConfigValidationInput FromUpdate(UpdateReportCommand command) => new()
    {
        Columns = command.Columns,
        SortFields = command.SortFields,
        FilterTree = command.FilterTree,
        GroupByFieldId = command.GroupByFieldId,
        GroupByMode = command.GroupByMode,
        HideTotals = command.HideTotals,
        GroupDefaultCollapsed = command.GroupDefaultCollapsed,
        GroupByDescending = command.GroupByDescending,
        Aggregations = command.Aggregations,
        DynamicFilterType = command.DynamicFilterType,
        CustomDynamicFilterFields = command.CustomDynamicFilterFields,
        CustomDynamicFilterItems = command.CustomDynamicFilterItems,
        Chart = command.Chart,
        ColumnsMode = command.ColumnsMode,
        TableSortGroup = command.TableSortGroup ?? [],
        Options = command.Options,
    };
}
