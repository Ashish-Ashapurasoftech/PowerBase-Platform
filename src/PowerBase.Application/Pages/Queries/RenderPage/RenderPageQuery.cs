namespace PowerBase.Application.Pages.Queries.RenderPage;

public record RenderPageQuery(
    Guid PagePublicId,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FilterValues = null,
    /// <summary>Search widget id → typed text. Applied as QuickSearch to whichever Report
    /// widget that Search widget targets (DashboardWidget.SearchTargetWidgetId).</summary>
    IReadOnlyDictionary<string, string>? SearchValues = null);
