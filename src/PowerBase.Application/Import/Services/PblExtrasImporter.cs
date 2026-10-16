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

    private static string Describe(Exception ex) =>
        ex is Domain.Exceptions.ValidationException ve
            ? string.Join(" ", ve.Errors.SelectMany(e => e.Value))
            : ex.Message;
}
