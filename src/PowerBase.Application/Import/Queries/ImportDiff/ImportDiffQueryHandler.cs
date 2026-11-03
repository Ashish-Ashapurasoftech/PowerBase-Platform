using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;

namespace PowerBase.Application.Import.Queries.ImportDiff;

/// <summary>Dry run for "Update Existing App": shows what importing the file would add, change
/// or leave alone, without writing anything.</summary>
public class ImportDiffQueryHandler
{
    private const string ADMINISTRATOR_ROLE = "Administrator";

    private readonly PblValidator _validator;
    private readonly ImportDiffBuilder _diffBuilder;
    private readonly IAppAccessService _accessService;

    public ImportDiffQueryHandler(PblValidator validator, ImportDiffBuilder diffBuilder, IAppAccessService accessService)
    {
        _validator = validator;
        _diffBuilder = diffBuilder;
        _accessService = accessService;
    }

    public async Task<ImportDiffResult> HandleAsync(ImportDiffQuery query, CancellationToken ct = default)
    {
        await _accessService.RequireAppRoleAsync(query.TargetAppPublicId, ADMINISTRATOR_ROLE, ct);

        var (parsedApp, warnings) = PblDocumentTools.LoadApp(query.Content, _validator);
        var document = parsedApp.Document;
        PblDocumentTools.ExcludeTables(document, query.Mapping?.ExcludedTableRefs);

        var diff = await _diffBuilder.BuildAsync(document, query.TargetAppPublicId, query.Mapping, ct, parsedApp.Pipelines);
        foreach (var unreadable in (parsedApp.Pipelines ?? []).Where(p => p.IsUnreadable))
            warnings.AddRange(unreadable.Issues.Select(i => new PowerBase.Application.Import.Commands.ImportAppFromPbl.ImportSkippedItem { Name = unreadable.Name, Reason = i.Message }));
        diff.Warnings.AddRange(warnings.Select(w => w.Reason));
        return diff;
    }
}
