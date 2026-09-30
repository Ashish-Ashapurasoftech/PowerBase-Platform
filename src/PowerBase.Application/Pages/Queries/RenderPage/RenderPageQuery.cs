namespace PowerBase.Application.Pages.Queries.RenderPage;

/// <summary>One filter slot's live value at render time — an operator + value-mode + value, the
/// same vocabulary the Advanced filter builder / "ask the user" prompt already use (see
/// filter-condition-operators.ts on the frontend), just chosen through the simplified Date
/// range/Text/User picker instead of the full operator dropdown. `ValueMode` is null/"literal"
/// for Text/User; a Date range preset sets it to "today"/"duringCurrent"/"duringPrevious"/
/// "duringNext" (RunReportQueryHandler.ResolveDateValueModeConditions resolves these to an actual
/// date/range on every run, same as the Advanced builder). `Value` carries a plain literal for
/// most operators, or "count:unit" for during/notDuring, or (only for the synthetic "dateRange"
/// operator) "startIso|endIso" for a custom range.</summary>
public record DashboardFilterValue(string Operator, string? Value, string? ValueMode = null);

public record RenderPageQuery(
    Guid PagePublicId,
    IReadOnlyDictionary<string, DashboardFilterValue>? FilterValues = null,
    /// <summary>Search widget id → typed text. Applied as QuickSearch to whichever Report
    /// widget that Search widget targets (DashboardWidget.SearchTargetWidgetId).</summary>
    IReadOnlyDictionary<string, string>? SearchValues = null);
