using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Imports a QBL file that holds only pipelines (no <c>QB::Application</c>) into apps that already exist — typically apps
/// imported earlier from the matching application export. The file's Quickbase table ids are matched to the user's tables
/// (<see cref="QblPipelineOnlyBinder"/>); the pipelines are then converted and created by the normal pipeline import, and a
/// pipeline whose steps use tables of several apps keeps each step on its own app's table. No app, table or field is changed.
/// </summary>
public class PipelineOnlyImportService(
    IAppRepository appRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
    IAppAccessService accessService, IQueryContext queryContext, PblPipelinesImporter? pipelinesImporter = null)
{
    private const string ADMINISTRATOR_ROLE = "Administrator";

    public sealed record Prepared(PipelineOnlyBinding Binding, IReadOnlyList<ParsedApp> Apps);

    /// <summary>Matches the file to the apps the user administers and converts its pipelines, without writing anything.</summary>
    public virtual async Task<Prepared> PrepareAsync(string content, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(ct);
        var binding = QblPipelineOnlyBinder.Bind(content, candidates);
        if (binding.Apps.Count == 0)
            return new Prepared(binding, []);

        var qbl = QblSerializer.Deserialize(binding.Content);
        var pipelines = QblPipelineConverter.ConvertAll(qbl, binding.Content);
        var appRefs = binding.Apps.Select(a => a.AppRef).ToList();
        var parsed = binding.Apps
            .Select((a, i) => new ParsedApp(a.AppRef, Document(a.AppRef, a.App), [], QblPipelineConverter.ForApp(pipelines, appRefs, i)))
            .ToList();
        return new Prepared(binding, parsed);
    }

    /// <summary>Creates the file's pipelines (turned off, like every imported PowerFlow unless <paramref name="activate"/>).</summary>
    public virtual async Task<ImportBatchReport> ImportAsync(string content, bool activate, CancellationToken ct)
    {
        var prepared = await PrepareAsync(content, ct);
        if (prepared.Apps.Count == 0)
            throw new BadRequestException("PIPELINE_ONLY_NO_APP", NoAppMessage(prepared.Binding));

        var imported = prepared.Apps
            .Select(app => (App: app, Report: new ImportReport
            {
                AppPublicId = prepared.Binding.Apps.First(a => a.AppRef == app.AppRef).App.PublicId,
                AppName = prepared.Binding.Apps.First(a => a.AppRef == app.AppRef).App.Name,
                IntoExistingApp = true,
            }))
            .ToList();

        foreach (var note in prepared.Binding.Notes)
            imported[0].Report.Skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = "Pipeline-only import", Reason = note });
        foreach (var table in prepared.Binding.UnmatchedTables)
            imported[0].Report.Skipped.Add(new ImportSkippedItem
            {
                LogicalRef = table, Name = $"Quickbase table {table}",
                Reason = "No existing table matches this Quickbase table; its steps were imported without a table. Choose it in the step.",
            });

        if (pipelinesImporter is not null)
            await pipelinesImporter.ImportAsync(prepared.Apps, imported, ct, activate);

        var batch = new ImportBatchReport();
        batch.Apps.AddRange(imported.Select(i => i.Report));
        return batch;
    }

    public static string NoAppMessage(PipelineOnlyBinding binding) =>
        "This file holds only pipelines, and none of its tables matches a table of an app you administer. "
        + "Import the application file first, then this file."
        + (binding.UnmatchedTables.Count > 0 ? $" Quickbase tables in the file: {string.Join(", ", binding.UnmatchedTables)}." : "");

    /// <summary>The apps the user may add PowerFlows to (Administrator of the app), with their tables and fields.</summary>
    private async Task<IReadOnlyList<PipelineOnlyApp>> CandidatesAsync(CancellationToken ct)
    {
        var result = new List<PipelineOnlyApp>();
        foreach (var app in await appRepo.ListAllByUserAsync(queryContext.UserId, ct))
        {
            try { await accessService.RequireAppRoleAsync(app.PublicId, ADMINISTRATOR_ROLE, ct); }
            catch (DomainException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var tables = new List<PipelineOnlyTable>();
            foreach (var table in await tableRepo.ListByAppAsync(app.Id, ct))
            {
                var fields = (await fieldRepo.ListByTableAsync(table.Id, ct))
                    .Where(f => !f.IsDeleted && f.Fid is not null)
                    .Select(f => new PipelineOnlyField(f.Fid!.Value, f.Name, f.Label ?? f.Name, f.TypeCode, f.IsSystem))
                    .ToList();
                var keyFid = table.KeyFieldId is { } keyId ? (await fieldRepo.ListByTableAsync(table.Id, ct)).FirstOrDefault(f => f.Id == keyId)?.Fid : null;
                tables.Add(new PipelineOnlyTable(table.Id, table.Name, keyFid, fields));
            }
            result.Add(new PipelineOnlyApp(app.PublicId, app.Id, app.Name, tables));
        }
        return result;
    }

    /// <summary>What the pipeline importer needs to find the existing tables and fields: each table and field under the refs the
    /// rewritten file uses, with the names they have in the app.</summary>
    private static PblDocument Document(string appRef, PipelineOnlyApp app) => new()
    {
        App = new PblApp { LogicalRef = appRef, Name = app.Name },
        Tables = app.Tables.Select(t => new PblTable
        {
            LogicalRef = $"$Table_PB_{t.Id}",
            Name = t.Name,
            Fields = t.Fields.Where(f => !f.IsSystem)
                .Select(f => new PblField { LogicalRef = $"$Table_PB_{t.Id}::$Field_{f.Fid}", Name = f.Name, Label = f.Label, TypeCode = f.TypeCode })
                .ToList(),
        }).ToList(),
    };
}
