using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Pages.Commands.CreatePage;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>Creates the app-level extras a PBL document can carry — app variables and code
/// pages — through the same command handlers the UI uses, so every normal validation and
/// permission gate still applies. A failure on one element is reported and skipped; it never
/// aborts the import.</summary>
public class PblExtrasImporter
{
    private const string CODE_PAGE_CONTENT_TYPE = "html";

    private readonly CreateAppVariableCommandHandler _createVariableHandler;
    private readonly UpdateAppVariableCommandHandler _updateVariableHandler;
    private readonly CreatePageCommandHandler _createPageHandler;
    private readonly IAppRepository _appRepo;
    private readonly IAppVariableRepository _variableRepo;

    public PblExtrasImporter(
        CreateAppVariableCommandHandler createVariableHandler,
        UpdateAppVariableCommandHandler updateVariableHandler,
        CreatePageCommandHandler createPageHandler,
        IAppRepository appRepo,
        IAppVariableRepository variableRepo)
    {
        _createVariableHandler = createVariableHandler;
        _updateVariableHandler = updateVariableHandler;
        _createPageHandler = createPageHandler;
        _appRepo = appRepo;
        _variableRepo = variableRepo;
    }

    /// <summary>The app's variables as a formula variable schema (what <c>[Name]</c> resolves against).</summary>
    public async Task<PowerBase.Formula.Binding.IVariableSchema> BuildVariableSchemaAsync(long appId, CancellationToken ct) =>
        new PowerBase.Application.Formulas.AppVariableSchema(await _variableRepo.ListAsync(appId, ct));

    public async Task<int> CreateVariablesAsync(IEnumerable<PblVariable> variables, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var created = 0;
        foreach (var variable in variables)
        {
            try
            {
                await _createVariableHandler.HandleAsync(new CreateAppVariableCommand(appPublicId, variable.Name, variable.Value, variable.Description), ct);
                created++;
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = variable.LogicalRef, Name = variable.Name, Reason = $"Variable not created: {Describe(ex)}" });
            }
        }
        return created;
    }

    /// <summary>Overwrites an existing variable's value/description (Update mode, user-approved).</summary>
    public async Task<bool> OverwriteVariableAsync(PblVariable variable, Guid appPublicId, Guid existingPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        try
        {
            await _updateVariableHandler.HandleAsync(new UpdateAppVariableCommand(appPublicId, existingPublicId, variable.Name, variable.Value, variable.Description), ct);
            return true;
        }
        catch (DomainException ex)
        {
            skipped.Add(new ImportSkippedItem { LogicalRef = variable.LogicalRef, Name = variable.Name, Reason = $"Variable not updated: {Describe(ex)}" });
            return false;
        }
    }

    public async Task<int> CreatePagesAsync(IEnumerable<PblPage> pages, Guid appPublicId, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var created = 0;
        foreach (var page in pages)
        {
            try
            {
                await _createPageHandler.HandleAsync(new CreatePageCommand(
                    appPublicId,
                    PageTypes.Code,
                    page.Name,
                    Description: null,
                    Visibility: "Shared",
                    VisibleToRoleIds: null,
                    Definition: null,
                    ContentType: CODE_PAGE_CONTENT_TYPE,
                    CodeHtml: page.Code,
                    CodeCss: null,
                    CodeJs: null,
                    ShowInNav: false,
                    NavOrder: 0,
                    NavIcon: null), ct);
                created++;
            }
            catch (UnauthorizedActionException ex)
            {
                // Code pages are gated per tenant and per user; report it rather than fail the import.
                skipped.Add(new ImportSkippedItem { LogicalRef = page.LogicalRef, Name = page.Name, Reason = $"Code page not created: {ex.Message}" });
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = page.LogicalRef, Name = page.Name, Reason = $"Code page not created: {Describe(ex)}" });
            }
        }
        return created;
    }

    /// <summary>Creates each dashboard as a PowerBase dashboard page. A report widget needs its report to exist (<paramref name="reportIdByRef"/>
    /// maps the converted report's ref to the report created for it); a widget whose report was not created is left out and reported.</summary>
    public async Task<int> CreateDashboardsAsync(
        IEnumerable<PblDashboard> dashboards, Guid appPublicId, IReadOnlyDictionary<string, Guid> reportIdByRef, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var created = 0;
        var order = 0;
        foreach (var dashboard in dashboards)
        {
            var definition = new PowerBase.Application.Pages.DashboardDefinition { Version = 2 };
            var tabNumber = 0;
            foreach (var tab in dashboard.Tabs)
            {
                tabNumber++;
                var widgets = new List<PowerBase.Application.Pages.DashboardWidget>();
                foreach (var w in tab.Widgets)
                {
                    Guid? reportId = w.ReportRef is not null && reportIdByRef.TryGetValue(w.ReportRef, out var id) ? id : null;
                    if (w.ReportRef is not null && reportId is null)
                    {
                        skipped.Add(new ImportSkippedItem { LogicalRef = dashboard.LogicalRef, Name = dashboard.Name, Reason = $"Dashboard widget skipped: its report ('{w.ReportRef.Split("::").Last()}') was not created." });
                        continue;
                    }

                    var widget = new PowerBase.Application.Pages.DashboardWidget
                    {
                        Id = $"w{widgets.Count + 1}",
                        WidgetType = w.WidgetType,
                        Title = w.Title,
                        ShowTitle = w.ShowTitle,
                        Layout = new PowerBase.Application.Pages.DashboardWidgetLayout { X = w.X, Y = w.Y, W = w.W, H = w.H },
                    };
                    switch (w.WidgetType)
                    {
                        case PowerBase.Application.Pages.DashboardWidgetTypes.Report:
                            widget.ReportPublicId = reportId!.Value;
                            break;
                        case PowerBase.Application.Pages.DashboardWidgetTypes.Text:
                            widget.Content = w.Content;
                            break;
                        case PowerBase.Application.Pages.DashboardWidgetTypes.WebPage:
                            widget.WebPageUrl = w.Url;
                            break;
                        case PowerBase.Application.Pages.DashboardWidgetTypes.ButtonBar:
                            widget.Buttons.Add(new PowerBase.Application.Pages.DashboardBarButton
                            {
                                Id = "b1",
                                Label = w.ButtonLabel ?? "Open",
                                ActionType = reportId is not null ? "NavigateToReport" : "OpenUrl",
                                Url = reportId is null ? w.Url : null,
                                TargetReportId = reportId,
                                OpenInNewTab = w.OpenInNewTab,
                                BackgroundColor = w.ButtonColor,
                            });
                            break;
                    }
                    widgets.Add(widget);
                }
                definition.Tabs.Add(new PowerBase.Application.Pages.DashboardTab { Id = $"tab-{tabNumber}", Name = tab.Name, Widgets = widgets });
            }

            try
            {
                await _createPageHandler.HandleAsync(new CreatePageCommand(
                    appPublicId, PageTypes.Dashboard, dashboard.Name, null, "Shared", null,
                    System.Text.Json.JsonSerializer.Serialize(definition, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }),
                    null, null, null, null, ShowInNav: true, NavOrder: order++, NavIcon: null), ct);
                created++;
            }
            catch (DomainException ex)
            {
                skipped.Add(new ImportSkippedItem { LogicalRef = dashboard.LogicalRef, Name = dashboard.Name, Reason = $"Dashboard not created: {Describe(ex)}" });
            }
        }
        return created;
    }

    private static string Describe(Exception ex) =>
        ex is Domain.Exceptions.ValidationException ve
            ? string.Join(" ", ve.Errors.SelectMany(e => e.Value))
            : ex.Message;
}
