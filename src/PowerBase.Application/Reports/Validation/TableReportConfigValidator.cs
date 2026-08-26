using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Table reports use: Columns/ColumnsMode, SortFields (legacy fallback), TableSortGroup (the
/// unified Sort+Group list — RunReportQueryHandler.RunTableAsync derives its effective group
/// field/sort order from this when non-empty, falling back to GroupByFieldId/SortFields for
/// reports saved before this existed), FilterTree, HideTotals, GroupDefaultCollapsed, Options,
/// dynamic-filter fields. They do NOT use Aggregations or Chart — RunTableAsync never reads
/// either, so a populated value there is dead/misleading data, rejected instead of silently
/// accepted (per "each report type must only allow settings applicable to that type").
/// </summary>
public sealed class TableReportConfigValidator : IReportConfigValidator
{
    /// <summary>Defensive sanity cap on the number of Sort+Group levels — not a product
    /// requirement (unlike the filter tree's 3-level nesting cap), just a guard against a
    /// pathological payload.</summary>
    private const int MaxSortGroupLevels = 10;

    private static readonly HashSet<string> AllowedColumnsModes = new(StringComparer.OrdinalIgnoreCase) { "Default", "Custom" };
    private static readonly HashSet<string> AllowedColumnHeaderTextModes = new(StringComparer.OrdinalIgnoreCase) { "Default", "Truncate", "Wrap" };

    public string ReportType => "Table";

    public IDictionary<string, string[]> Validate(ReportConfigValidationInput input, IReadOnlyList<AppField> tableFields)
    {
        var errors = new Dictionary<string, string[]>();
        var validFieldIds = CommonReportValidationHelpers.GetValidFieldIds(tableFields);

        CommonReportValidationHelpers.ValidateColumns(input.Columns, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateFilterGroup(input.FilterTree, validFieldIds, errors);
        CommonReportValidationHelpers.ValidateDynamicFilterFields(input, validFieldIds, errors);

        if (!AllowedColumnsModes.Contains(input.ColumnsMode))
            CommonReportValidationHelpers.AddError(errors, "columnsMode", $"columnsMode must be one of: {string.Join(", ", AllowedColumnsModes)}");

        if (input.GroupByFieldId.HasValue && !validFieldIds.Contains(input.GroupByFieldId.Value))
            CommonReportValidationHelpers.AddError(errors, "groupByFieldId", $"Unknown field ID: {input.GroupByFieldId.Value}");

        ValidateTableSortGroup(input.TableSortGroup, validFieldIds, errors);
        ValidateOptions(input.Options, errors);

        CommonReportValidationHelpers.ForbidIfPopulated(input.Aggregations.Count > 0, "aggregations", "Table", errors);
        CommonReportValidationHelpers.ForbidIfPopulated(input.Chart is not null, "chart", "Table", errors);

        return errors;
    }

    private static void ValidateTableSortGroup(List<SortGroupLevelCommand> levels, HashSet<long> validFieldIds, IDictionary<string, string[]> errors)
    {
        if (levels.Count == 0)
            return;

        if (levels.Count > MaxSortGroupLevels)
            CommonReportValidationHelpers.AddError(errors, "tableSortGroup", $"A report may have at most {MaxSortGroupLevels} sort/group levels.");

        foreach (var level in levels)
        {
            if (!validFieldIds.Contains(level.FieldId))
                CommonReportValidationHelpers.AddError(errors, "tableSortGroup", $"Unknown field ID: {level.FieldId}");
        }
    }

    private static void ValidateOptions(ReportOptionsCommand? options, IDictionary<string, string[]> errors)
    {
        if (options is null)
            return;

        if (!AllowedColumnHeaderTextModes.Contains(options.ColumnHeaderText))
            CommonReportValidationHelpers.AddError(errors, "options.columnHeaderText",
                $"columnHeaderText must be one of: {string.Join(", ", AllowedColumnHeaderTextModes)}");
    }
}
