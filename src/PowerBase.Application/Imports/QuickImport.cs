using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>What the table's own Import dialog sends: the import as it was set up on the screen, the file it reads (a file import), and who else to tell.</summary>
public sealed record QuickImportRequest(ImportDefinitionConfig Config, Guid? FileId, IReadOnlyList<string>? NotifyEmails);

/// <summary>An import started straight from a table, with no template to keep. It is the template import under another door, not a second engine: the
/// definition is saved through <see cref="SaveImportDefinitionHandler"/> (so every check a saved import goes through runs, permissions included), the run is
/// started through <see cref="StartImportRunHandler"/>, and the definition is then removed again. A removed definition is an ordinary state: the run keeps
/// working, shows in the history and on the run page, and no list of templates (the app's Imports, PowerFlow, pipelines, the scheduler) ever shows it.
/// <para>One table, no schedule and nothing that only makes sense for a template that comes back (reading only what changed since the last run).</para></summary>
public sealed class StartQuickImportHandler(
    SaveImportDefinitionHandler save, StartImportRunHandler start, IImportDefinitionRepository definitions, IAppAccessService access,
    IAppTableRepository tables, IQueryContext user, ILogger<StartQuickImportHandler> logger)
{
    public const string NamePrefix = "Quick import - ";

    public async Task<ImportRunStart> HandleAsync(Guid destinationTableId, QuickImportRequest request, string? frontendBaseUrl, CancellationToken ct)
    {
        await access.RequireMembershipByTablePublicIdAsync(destinationTableId, ct);
        var cfg = request.Config ?? throw Invalid("The import is missing.");
        if (cfg.Schedule is not null) throw Invalid("A quick import runs once, now; it cannot be scheduled.");
        if (cfg.AdditionalTargets.Count > 0) throw Invalid("A quick import fills this table only.");
        if (cfg.OnlyChangedSinceLastRun || cfg.ChangedSinceUtc is not null) throw Invalid("Reading only what changed since the last run belongs to saved imports.");
        if (cfg.SourceKind == ImportSourceKinds.File && request.FileId is null) throw Invalid("Choose the file to import.");
        if (cfg.SourceKind == ImportSourceKinds.Table && request.FileId is not null) throw Invalid("This import reads a table, not a file.");

        cfg.Name = await NameAsync(cfg, ct);
        var publicId = await save.HandleAsync(destinationTableId, null, cfg, ct);
        // The definition is only the carrier of this one run: it goes again whatever happens to the start.
        var definition = await definitions.GetByPublicIdAsync(publicId, ct) ?? throw new NotFoundException("ImportDefinition", publicId);
        try
        {
            return await start.StartAsync(publicId, ImportTrigger.Manual, request.NotifyEmails, frontendBaseUrl, null, null, request.FileId, ct);
        }
        finally
        {
            try { await definitions.DeleteAsync(definition.Id, user.UserId, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The run has started (or the error is the one the caller should see): a leftover template is not worth hiding either.
                logger.LogWarning(ex, "The temporary definition {DefinitionId} of a quick import could not be removed.", definition.Id);
            }
        }
    }

    /// <summary>A name the history can show: what was imported from (the screen sends the file's own name as the name of a file import).</summary>
    private async Task<string> NameAsync(ImportDefinitionConfig cfg, CancellationToken ct)
    {
        if (cfg.SourceKind == ImportSourceKinds.File) return Cap(NamePrefix + (string.IsNullOrWhiteSpace(cfg.Name) ? "file" : cfg.Name.Trim()));
        var source = "another table";
        try { source = (await tables.GetByPublicIdAsync(cfg.SourceTableId, ct)).Name; }
        catch (NotFoundException) { /* the plan builder reports a source that is not there */ }
        return Cap(NamePrefix + source);
    }

    private static string Cap(string name) => name.Length <= 200 ? name : name[..200];

    private static ValidationException Invalid(string message) => new(new Dictionary<string, string[]> { ["Import"] = [message] });
}
