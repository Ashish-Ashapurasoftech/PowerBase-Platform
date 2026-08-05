using System.Text.Json;

namespace PowerBase.Application.Pages;

/// <summary>Deserialized shape of a Dashboard-type Page's Definition JSON blob. Cross-entity
/// references (ReportPublicId) are GUIDs, never internal BIGINT ids, so a future copy-with-app
/// engine can remap them with a single old→new dictionary.</summary>
public class DashboardDefinition
{
    public int Version { get; set; } = 2;

    /// <summary>Page-level filter slots, shared across every tab. Each widget maps a slot to
    /// a field on its own table via <see cref="DashboardWidget.FilterBindings"/> — a widget
    /// that doesn't bind a slot simply ignores that filter.</summary>
    public List<DashboardFilterSlot> Filters { get; set; } = [];

    public List<DashboardTab> Tabs { get; set; } = [];

    // ── Legacy (Version 1, pre-tabs) shape — kept only so old saved Definitions still
    // deserialize. Never populated on a Version-2+ definition; use Tabs instead.
    public DashboardGrid? Grid { get; set; }
    public List<DashboardWidget>? Widgets { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses a Page's raw Definition JSON and normalizes it to the current
    /// (tabbed) shape. A Version-1 definition (flat Grid+Widgets, no Tabs) is wrapped into a
    /// single default tab so every existing saved Dashboard keeps working unchanged.
    /// Case-insensitive: the frontend writes this JSON with camelCase keys.</summary>
    public static DashboardDefinition Parse(string json)
    {
        var def = JsonSerializer.Deserialize<DashboardDefinition>(json, JsonOptions) ?? new DashboardDefinition();

        if (def.Tabs.Count == 0 && def.Widgets != null)
        {
            def.Tabs =
            [
                new DashboardTab
                {
                    Id = "tab-1",
                    Name = "Tab 1",
                    Grid = def.Grid ?? new DashboardGrid(),
                    Widgets = def.Widgets,
                },
            ];
        }

        if (def.Tabs.Count == 0)
        {
            def.Tabs = [new DashboardTab { Id = "tab-1", Name = "Tab 1" }];
        }

        def.Grid = null;
        def.Widgets = null;
        return def;
    }
}

public class DashboardTab
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DashboardGrid Grid { get; set; } = new();
    public List<DashboardWidget> Widgets { get; set; } = [];
}

public class DashboardGrid
{
    public int Cols { get; set; } = 12;
    public int RowHeight { get; set; } = 60;
    public int Gap { get; set; } = 8;
}

public class DashboardFilterSlot
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string InputType { get; set; } = "select";
    public string? DefaultValue { get; set; }
}

public static class DashboardWidgetTypes
{
    public const string Report = "Report";
    public const string Text = "Text";
    public const string ButtonBar = "ButtonBar";
    public const string WebPage = "WebPage";
    public const string Search = "Search";
    public const string Spacer = "Spacer";
    public static readonly string[] All = [Report, Text, ButtonBar, WebPage, Search, Spacer];

    /// <summary>LinkBar was replaced by WebPage — kept here (not in <see cref="All"/>, so the
    /// designer can no longer create one) purely so any already-saved LinkBar widget still
    /// deserializes and renders instead of silently disappearing.</summary>
    public const string LinkBar = "LinkBar";
}

public class DashboardWidget
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Defaults to Report so every widget saved before this field existed still
    /// deserializes as a report/chart widget, matching its only prior meaning.</summary>
    public string WidgetType { get; set; } = DashboardWidgetTypes.Report;
    public string Title { get; set; } = string.Empty;
    public bool ShowTitle { get; set; } = true;
    public DashboardWidgetLayout Layout { get; set; } = new();
    /// <summary>Background color (CSS color string), used by Search/WebPage/Spacer today.</summary>
    public string? BackgroundColor { get; set; }

    // ── Report widget ───────────────────────────────────────────────────────────
    public Guid ReportPublicId { get; set; }
    public int PageSize { get; set; } = 10;
    /// <summary>Slot key → field id on this widget's own report/table.</summary>
    public Dictionary<string, DashboardFilterBinding> FilterBindings { get; set; } = [];

    // ── Text widget ─────────────────────────────────────────────────────────────
    public string? Content { get; set; }

    // ── ButtonBar widget ────────────────────────────────────────────────────────
    public List<DashboardBarButton> Buttons { get; set; } = [];

    // ── WebPage widget ──────────────────────────────────────────────────────────
    public string? WebPageUrl { get; set; }

    // ── LinkBar widget (legacy, read-only going forward) ───────────────────────
    public List<DashboardBarLink> Links { get; set; } = [];

    // ── Search widget ───────────────────────────────────────────────────────────
    public Guid? SearchTargetTableId { get; set; }
    /// <summary>AllFields | SelectedFields | Report</summary>
    public string SearchScope { get; set; } = "AllFields";
    /// <summary>Field ids searched when SearchScope == SelectedFields.</summary>
    public List<long> SearchFieldIds { get; set; } = [];
    /// <summary>SearchAllFields | SearchSingleField — only meaningful when SearchScope ==
    /// SelectedFields: whether the box searches all of SearchFieldIds at once, or lets the
    /// viewer choose one of them via a dropdown next to the input.</summary>
    public string SearchBoxStyle { get; set; } = "SearchAllFields";
    /// <summary>Report run when SearchScope == Report.</summary>
    public Guid? SearchReportId { get; set; }
    /// <summary>Partial | Exact | UserChoice</summary>
    public string SearchType { get; set; } = "Partial";
    /// <summary>Popup | NewWindow</summary>
    public string SearchShowResultsIn { get; set; } = "NewWindow";
    public bool SearchShowTitle { get; set; }
    public bool SearchShowHintText { get; set; }
    public string? SearchPlaceholder { get; set; }
    /// <summary>Legacy (pre-redesign) Search widgets targeted another Report widget on the
    /// same tab directly, feeding text into its RunReportQuery via RenderPageQuery.SearchValues.
    /// Kept only so old saved widgets still parse; the current designer no longer sets this.</summary>
    public string? SearchTargetWidgetId { get; set; }

    // ── Spacer widget ───────────────────────────────────────────────────────────
    public bool SpacerShowDivider { get; set; }
}

public class DashboardBarButton
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Icon { get; set; }
    /// <summary>OpenUrl | NavigateToPage | NavigateToReport | CreateNewRecord</summary>
    public string ActionType { get; set; } = "OpenUrl";
    public string? Url { get; set; }
    public Guid? TargetPageId { get; set; }
    public Guid? TargetReportId { get; set; }
    /// <summary>Table a CreateNewRecord button opens the add-record form for.</summary>
    public Guid? TargetTableId { get; set; }
    public bool OpenInNewTab { get; set; }
    public string? BackgroundColor { get; set; }
    /// <summary>left | center | right</summary>
    public string IconAlign { get; set; } = "center";
}

public class DashboardBarLink
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool OpenInNewTab { get; set; } = true;
}

public class DashboardFilterBinding
{
    public long FieldId { get; set; }
    public string? SubField { get; set; }
}

public class DashboardWidgetLayout
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 4;
    public int H { get; set; } = 4;
}
