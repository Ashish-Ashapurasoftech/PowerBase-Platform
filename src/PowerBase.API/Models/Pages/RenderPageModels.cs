using PowerBase.API.Models.Reports;

namespace PowerBase.API.Models.Pages;

public class RenderPageRequest
{
    /// <summary>Page-level filter slot key → selected value(s).</summary>
    public Dictionary<string, List<string>>? FilterValues { get; set; }
    /// <summary>Search widget id → typed text.</summary>
    public Dictionary<string, string>? SearchValues { get; set; }
}

public class DashboardGridResponse
{
    public int Cols { get; set; }
    public int RowHeight { get; set; }
    public int Gap { get; set; }
}

public class DashboardFilterSlotResponse
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string InputType { get; set; } = "select";
    public string? DefaultValue { get; set; }
}

public class DashboardWidgetLayoutResponse
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public class DashboardBarButtonResponse
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string ActionType { get; set; } = "OpenUrl";
    public string? Url { get; set; }
    public Guid? TargetPageId { get; set; }
    public Guid? TargetReportId { get; set; }
    public Guid? TargetTableId { get; set; }
    public bool OpenInNewTab { get; set; }
    public string? BackgroundColor { get; set; }
    public string IconAlign { get; set; } = "center";
}

public class DashboardBarLinkResponse
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool OpenInNewTab { get; set; } = true;
}

public class RenderedWidgetResponse
{
    public string WidgetId { get; set; } = string.Empty;
    public string WidgetType { get; set; } = "Report";
    public string Status { get; set; } = "ok"; // ok | forbidden | error
    public string? Message { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool ShowTitle { get; set; }
    public DashboardWidgetLayoutResponse Layout { get; set; } = new();
    public string? BackgroundColor { get; set; }

    // Report widget only
    public Guid ReportPublicId { get; set; }
    public Guid TableId { get; set; }
    public string ReportType { get; set; } = "Table";
    public ChartConfigDto? Chart { get; set; }
    public ReportRunResponse? Result { get; set; }

    // Text widget only
    public string? Content { get; set; }

    // ButtonBar widget only
    public IReadOnlyList<DashboardBarButtonResponse> Buttons { get; set; } = [];

    // WebPage widget only
    public string? WebPageUrl { get; set; }

    // LinkBar widget only (legacy)
    public IReadOnlyList<DashboardBarLinkResponse> Links { get; set; } = [];

    // Search widget only
    public Guid? SearchTargetTableId { get; set; }
    public string SearchScope { get; set; } = "AllFields";
    public IReadOnlyList<long> SearchFieldIds { get; set; } = [];
    public string SearchBoxStyle { get; set; } = "SearchAllFields";
    public Guid? SearchReportId { get; set; }
    public string SearchType { get; set; } = "Partial";
    public string SearchShowResultsIn { get; set; } = "NewWindow";
    public bool SearchShowTitle { get; set; }
    public bool SearchShowHintText { get; set; }
    public string? SearchPlaceholder { get; set; }

    // Spacer widget only
    public bool SpacerShowDivider { get; set; }
}

public class RenderedTabResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DashboardGridResponse Grid { get; set; } = new();
    public IReadOnlyList<RenderedWidgetResponse> Widgets { get; set; } = [];
}

public class RenderPageResponse
{
    public IReadOnlyList<DashboardFilterSlotResponse> Filters { get; set; } = [];
    public IReadOnlyList<RenderedTabResponse> Tabs { get; set; } = [];
}
