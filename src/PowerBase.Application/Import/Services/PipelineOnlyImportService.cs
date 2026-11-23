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
    IAppAccessService accessService, IQueryContext queryContext, PblPipelinesImporter? pipelinesImporter = null,
    Microsoft.Extensions.Logging.ILogger<PipelineOnlyImportService>? logger = null)
{
    private const string ADMINISTRATOR_ROLE = "Administrator";

    /// <summary><paramref name="NotImported"/>: pipelines of the file left out because none of their tables or apps matches an
    /// app of the user (name, reason).</summary>
    public sealed record Prepared(PipelineOnlyBinding Binding, IReadOnlyList<ParsedApp> Apps, IReadOnlyList<(string Name, string Reason)> NotImported)
    {
        public Prepared(PipelineOnlyBinding binding, IReadOnlyList<ParsedApp> apps) : this(binding, apps, []) { }
    }

    /// <summary>Matches the file to the apps the user administers and converts its pipelines, without writing anything.</summary>
    public virtual async Task<Prepared> PrepareAsync(string content, CancellationToken ct)
    {
        var prepared = await PrepareCoreAsync(content, ct);
        Remember(content, prepared);
        return prepared;
    }

    // ------------------------------------------------------------------ preview → import hand-over
    // Matching a large file (1000+ pipelines against dozens of apps) takes many seconds. The preview's result is kept for a few
    // minutes so the Import that follows it uses exactly what was previewed instead of doing all of it again. Taken (removed) on
    // use, so it is never used twice; a new preview always works it out afresh.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Prepared Value)> Recent = new(StringComparer.Ordinal);
    private static readonly TimeSpan RECENT_TTL = TimeSpan.FromMinutes(15);

    private string KeyOf(string content) =>
        $"{queryContext.TenantId}:{queryContext.UserId}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))}";

    private void Remember(string content, Prepared prepared)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in Recent)
            if (now - entry.At > RECENT_TTL) Recent.TryRemove(key, out _);
        Recent[KeyOf(content)] = (now, prepared);
    }

    private async Task<Prepared> TakeOrPrepareAsync(string content, CancellationToken ct) =>
        Recent.TryRemove(KeyOf(content), out var entry) && DateTime.UtcNow - entry.At <= RECENT_TTL
            ? entry.Value
            : await PrepareCoreAsync(content, ct);

    private async Task<Prepared> PrepareCoreAsync(string content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var candidates = await CandidatesAsync(ct);
        var candidatesMs = sw.ElapsedMilliseconds; sw.Restart();
        var binding = QblPipelineOnlyBinder.Bind(content, candidates);
        var bindMs = sw.ElapsedMilliseconds; sw.Restart();
        if (binding.Apps.Count == 0)
            return new Prepared(binding, []);

        // The two reads of the file are independent: the plain read runs while the file is deserialized.
        var rawRead = Task.Run(() => QblPipelineConverter.ReadRaw(binding.Content), ct);
        var qbl = QblSerializer.Deserialize(binding.Content);
        var readMs = sw.ElapsedMilliseconds; sw.Restart();
        var pipelines = QblPipelineConverter.ConvertAll(qbl, binding.Content, skipStepsWithUnmatchedTable: true,
            raw: () => rawRead.GetAwaiter().GetResult());
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger ?? (Microsoft.Extensions.Logging.ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "Pipeline-only prepare: apps {Apps} ({Tables} tables) {CandidatesMs} ms, match {BindMs} ms, read {ReadMs} ms, convert {ConvertMs} ms",
            candidates.Count, candidates.Sum(c => c.Tables.Count), candidatesMs, bindMs, readMs, sw.ElapsedMilliseconds);
        var appRefs = binding.Apps.Select(a => a.AppRef).ToList();
        var (kept, notImported) = SplitByApp(pipelines, appRefs);
        var parsed = binding.Apps
            .Select((a, i) => new ParsedApp(a.AppRef, Document(a.AppRef, a.App), [], QblPipelineConverter.ForApp(kept, appRefs, i)))
            .ToList();
        return new Prepared(binding, parsed, notImported);
    }

    /// <summary>A pipeline goes into the app its trigger (or first step with a table) was matched to. A pipeline none of whose
    /// steps matched an app belongs to a Quickbase app that is not among the user's apps: it is left out (and reported) rather
    /// than put into an unrelated app.</summary>
    public static (List<PblPipeline> Kept, List<(string Name, string Reason)> NotImported) SplitByApp(
        IReadOnlyList<PblPipeline> pipelines, IReadOnlyList<string> appRefs)
    {
        var kept = new List<PblPipeline>();
        var notImported = new List<(string, string)>();
        foreach (var pipeline in pipelines)
        {
            // Its steps on the user's tables cannot start a PowerFlow (the trigger was on a table of a Quickbase app that was not
            // imported): an empty PowerFlow would be created, so it is reported instead.
            if (pipeline.AppRef is not null && appRefs.Contains(pipeline.AppRef, StringComparer.Ordinal)
                && pipeline.Issues.Any(i => i.Code == "PIPELINE_NO_VALID_START"))
                notImported.Add((pipeline.Name, "Its trigger / first step uses a table that is not in your apps (a Quickbase app that was not imported, or a clone of one), so the steps on your tables cannot start a PowerFlow. "
                    + string.Join(" ", pipeline.Issues.Where(i => i.Code is "PIPELINE_STEP_TABLE_NOT_MATCHED").Select(i => $"Skipped: '{i.StepName}'.").Distinct().Take(5))));
            else if (pipeline.AppRef is not null && appRefs.Contains(pipeline.AppRef, StringComparer.Ordinal)
                     && PipelineCompleteness.Reason(pipeline) is { } incomplete)
                notImported.Add((pipeline.Name, incomplete));
            else if (pipeline.AppRef is not null && appRefs.Contains(pipeline.AppRef, StringComparer.Ordinal))
                kept.Add(pipeline);
            else
                notImported.Add((pipeline.Name, pipeline.IsUnreadable
                    ? "The pipeline could not be read."
                    : "None of its Quickbase tables matches a table of an app you administer (its Quickbase app was not imported). Import that application first, then this file again."));
        }
        return (kept, notImported);
    }

    /// <summary>Creates the file's pipelines (turned off, like every imported PowerFlow unless <paramref name="activate"/>).</summary>
    public virtual async Task<ImportBatchReport> ImportAsync(string content, bool activate, CancellationToken ct)
    {
        var prepared = await TakeOrPrepareAsync(content, ct);
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
        foreach (var (name, reason) in prepared.NotImported)
            imported[0].Report.Skipped.Add(new ImportSkippedItem { LogicalRef = "", Name = name, Reason = reason, Code = "PIPELINE_ONLY_APP_NOT_FOUND" });
        foreach (var table in prepared.Binding.UnmatchedTables)
            imported[0].Report.Skipped.Add(new ImportSkippedItem
            {
                LogicalRef = table, Name = $"Quickbase table {table}",
                Reason = "No existing table matches this Quickbase table; its steps were skipped (its Quickbase app was not imported, or the table is a copy in another Quickbase app).",
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
            // All the app's fields in one read (one per table meant thousands of queries for a user with many apps).
            var byTable = (await fieldRepo.ListByAppAsync(app.Id, ct)).ToLookup(f => f.AppTableId);
            foreach (var table in await tableRepo.ListByAppAsync(app.Id, ct))
            {
                var all = byTable[table.Id].ToList();
                var fields = all
                    .Where(f => !f.IsDeleted && f.Fid is not null)
                    .Select(f => new PipelineOnlyField(f.Fid!.Value, f.Name, f.Label ?? f.Name, f.TypeCode, f.IsSystem, ResultTypeOf(f.TypeCode, f.Settings)))
                    .ToList();
                var keyFid = table.KeyFieldId is { } keyId ? all.FirstOrDefault(f => f.Id == keyId)?.Fid : null;
                tables.Add(new PipelineOnlyTable(table.Id, table.Name, keyFid, fields));
            }
            result.Add(new PipelineOnlyApp(app.PublicId, app.Id, app.Name, tables));
        }
        return result;
    }

    /// <summary>A Lookup / Summary field's result type (settings resultTypeCode); null for other fields.</summary>
    internal static string? ResultTypeOf(string typeCode, string? settings)
    {
        if (typeCode is not ("Lookup" or "Summary") || string.IsNullOrWhiteSpace(settings)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settings);
            return doc.RootElement.TryGetProperty("resultTypeCode", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String ? r.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
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
