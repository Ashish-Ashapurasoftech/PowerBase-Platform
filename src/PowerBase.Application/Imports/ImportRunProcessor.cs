using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

/// <summary>The definition as it was when a run was queued, so later edits never change a run in flight.</summary>
/// <param name="FrontendBaseUrl">The trusted address of the web app at the time the run was started, for the link in the
/// completion email (null when none was known).</param>
public sealed record ImportRunSnapshot(Guid DestinationTableId, ImportDefinitionConfig Config, string? FrontendBaseUrl = null, ImportRunFile? File = null);

/// <summary>Executes one queued run. The source is read in chunks; each chunk is checked row by row (see
/// <see cref="ImportChunkWriter"/>), the good rows are bulk-written and committed, and everything that was not imported is
/// recorded with its reason. A bad row never stops the run. Depending on the constraint policy the run first makes one
/// extra pass over the source: a scan for duplicate groups, or a complete dry run that writes nothing. A user can stop a run
/// at any time; it stops after the chunk it is on, and rows already imported stay. However a run ends, the people involved
/// are told.</summary>
public sealed class ImportRunProcessor(
    IImportRunRepository runs,
    ImportPlanBuilder planBuilder,
    ImportSourceReader reader,
    IImportDataStore store,
    IRecordRepository records,
    IAppTableRepository tables,
    IAppFieldRepository fieldRepo,
    FormulaEngine formulaEngine,
    IAuditRepository audit,
    IFileStorageService storage,
    IImportNotifier notifier,
    IImportFileAccess fileAccess,
    ImportMultiTargetRunner multiTarget,
    IOptions<ImportOptions> options,
    ILogger<ImportRunProcessor> logger)
{
    /// <summary>The issue table keeps this many rows per run for the run page; the feedback file has them all.</summary>
    private const int IssueCap = 5000;

    /// <summary>Totals of one pass over the source, plus the details file being written for it.</summary>
    private sealed class PassResult(ImportDetailsWriter details) : IAsyncDisposable
    {
        public ImportDetailsWriter Details { get; } = details;
        public List<ImportRunIssue> BufferedIssues { get; } = new();
        public long RowsRead, Inserted, Updated, Unchanged, Skipped, Errored, Cursor;
        /// <summary>A user asked for the run to stop; the pass ended early.</summary>
        public bool Cancelled { get; set; }
        public long Imported => Inserted + Updated;
        public ValueTask DisposeAsync() => Details.DisposeAsync();
    }

    public async Task RunAsync(Guid runPublicId, CancellationToken ct)
    {
        var run = await runs.GetByPublicIdAsync(runPublicId, ct) ?? throw new NotFoundException("ImportRun", runPublicId);
        if (run.Status is ImportRunStatus.Success or ImportRunStatus.Partial or ImportRunStatus.Failed or ImportRunStatus.Cancelled) return;
        var snapshot = ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson);

        // A run that is already 'running' was interrupted (worker crash/restart): resuming could write a chunk twice,
        // so it is failed with a clear message and the user starts a new run.
        if (run.Status == ImportRunStatus.Running)
        {
            await EndAsync(run, snapshot, null, ImportRunStatus.Failed,
                $"The import was interrupted after {run.RowsRead:N0} source rows ({run.Inserted + run.Updated:N0} imported). Start a new run.", null, ct);
            return;
        }

        // An import that fills several tables has its own runner; an import into one table (every import saved before) takes the path below.
        if (snapshot?.Config.AdditionalTargets is { Count: > 0 })
        {
            await multiTarget.RunAsync(run, snapshot, EndAsync, ct);
            return;
        }

        ImportPlan plan;
        try
        {
            if (snapshot is null) throw new InvalidOperationException("The run has no definition snapshot.");
            // A file import is planned against the columns of the file as it is now; a table import against the live tables.
            var file = snapshot.File is null ? null : await fileAccess.OpenSourceAsync(snapshot, countRows: true, ct);
            plan = await planBuilder.BuildAsync(snapshot.Config, snapshot.DestinationTableId, ct, file);
            await planBuilder.EnsureMergeKeyIsUniqueAsync(plan, ct);
            if (plan.File is { DataRows: 0 }) throw new ImportFileFormatException("The file has no rows to import below its column names.");
        }
        catch (Exception ex) when (ex is DomainException or ImportFileFormatException or FileNotFoundException or DirectoryNotFoundException)
        {
            var message = ex is FileNotFoundException or DirectoryNotFoundException ? "The uploaded file is no longer available. Upload it again." : ex.Message;
            await EndAsync(run, snapshot, null, ImportRunStatus.Failed, Truncate(message, 1000), null, ct);
            return;
        }

        var maxId = run.SourceMaxId ?? plan.File?.LastRow ?? await store.GetMaxRecordIdAsync(plan.Source, ct);
        // False when the run was cancelled while it waited in the queue: it must stay cancelled.
        if (!await runs.MarkRunningAsync(run.Id, maxId, ct)) return;
        await audit.LogActivityAsync("ImportStarted", "ImportRun", run.PublicId.ToString(), null, plan.Destination.AppId, ct: ct);
        var gate = await ImportDataRuleGate.CreateAsync(plan.Destination, plan.DestinationFields, tables, fieldRepo, records, formulaEngine, ct);

        try
        {
            Dictionary<int, HashSet<ulong>>? groups = null;
            var writeFrom = 0;
            if (plan.ConstraintPolicy == ImportConstraintPolicy.ExcludeDuplicateGroups)
            {
                groups = await ScanDuplicateGroupsAsync(plan, run, maxId, 0, 45, ct);
                if (groups is null)
                {
                    await EndAsync(run, snapshot, plan, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, ct);
                    return;
                }
                writeFrom = 45;
            }
            else if (plan.ConstraintPolicy == ImportConstraintPolicy.AbortIfAnyIssue)
            {
                await using var check = await ExecutePassAsync(plan, run, maxId, gate, ImportPassMode.Validate, null, 0, 50, ct);
                if (check.Cancelled)
                {
                    await EndAsync(run, snapshot, plan, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, ct);
                    return;
                }
                if (check.Errored > 0)
                {
                    await RejectRunAsync(plan, run, snapshot, check, ct);
                    return;
                }
                writeFrom = 50;
            }

            await using var final = await ExecutePassAsync(plan, run, maxId, gate, ImportPassMode.Write, groups, writeFrom, 100, ct);
            await CompleteRunAsync(plan, run, snapshot, final, ct);
        }
        catch (OperationCanceledException) { throw; } // shutdown: the lease expires and the run is failed as interrupted on pickup
        catch (Exception ex)
        {
            logger.LogError(ex, "Import run {RunId} failed.", run.PublicId);
            await EndAsync(run, snapshot, plan, ImportRunStatus.Failed, Truncate(ex.Message, 1000), null, CancellationToken.None);
        }
    }

    /// <summary>The rows of this plan's source, for one pass: the file when the import reads a file, otherwise the table.</summary>
    private async Task<IImportChunkReader> OpenSourceAsync(ImportPlan plan, CancellationToken ct)
    {
        IImportChunkReader source = plan.File is null ? reader : await fileAccess.OpenChunkReaderAsync(plan, ct);
        return plan.Virtuals.Count == 0 ? source : new ImportVirtualColumnReader(source, plan, formulaEngine);
    }

    /// <summary>Reads the whole source once, to learn which unique values occur more than once, so the writing pass can
    /// leave out each such group entirely. Writes nothing. Null when the run was stopped.</summary>
    private async Task<Dictionary<int, HashSet<ulong>>?> ScanDuplicateGroupsAsync(
        ImportPlan plan, ImportRun run, long maxId, int progressFrom, int progressTo, CancellationToken ct)
    {
        var tracker = new ImportDuplicateTracker();
        var writer = new ImportChunkWriter(plan, store, records, null, formulaEngine, run.TriggeredByUserId, ImportPassMode.Validate, tracker);
        await using var source = await OpenSourceAsync(plan, ct);
        long cursor = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await source.ReadChunkAsync(plan, cursor, maxId, ct);
            if (chunk.Exhausted) break;
            writer.CollectDuplicateGroups(chunk.Rows);
            cursor = chunk.LastId;
            if (await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), Progress(progressFrom, progressTo, cursor, maxId), ct))
                return null;
        }
        return tracker.DuplicateGroups;
    }

    /// <summary>One pass over the source: read a chunk, check and (in <see cref="ImportPassMode.Write"/>) write it, record
    /// what was rejected, and advance the run. A <see cref="ImportPassMode.Validate"/> pass does everything except the
    /// writes and leaves the run's counters alone. Recording a chunk also reports whether a user asked to stop; the pass then
    /// ends and is marked <see cref="PassResult.Cancelled"/>.</summary>
    private async Task<PassResult> ExecutePassAsync(
        ImportPlan plan, ImportRun run, long maxId, ImportDataRuleGate? gate, ImportPassMode mode,
        Dictionary<int, HashSet<ulong>>? groups, int progressFrom, int progressTo, CancellationToken ct)
    {
        var writer = new ImportChunkWriter(plan, store, records, gate, formulaEngine, run.TriggeredByUserId, mode, new ImportDuplicateTracker(groups));
        await writer.PrepareAsync(ct);
        var result = new PassResult(NewDetailsWriter(plan));
        await using var source = await OpenSourceAsync(plan, ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await source.ReadChunkAsync(plan, result.Cursor, maxId, ct);
                if (chunk.Exhausted) break;

                var outcome = chunk.Rows.Count == 0 ? ImportChunkOutcome.None : await writer.ProcessAsync(chunk.Rows, ct);
                result.Cursor = chunk.LastId;
                result.RowsRead += chunk.Rows.Count; result.Inserted += outcome.Inserted; result.Updated += outcome.Updated;
                result.Unchanged += outcome.Unchanged; result.Skipped += outcome.Skipped; result.Errored += outcome.Errored;
                await result.Details.AppendAsync(outcome.Feedback, outcome.Written, ct);

                var progress = Progress(progressFrom, progressTo, result.Cursor, maxId);
                bool stop;
                if (mode == ImportPassMode.Write)
                {
                    if (outcome.Inserted > 0) await store.AddRecordCountAsync(plan.Destination.Id, (int)outcome.Inserted, ct);
                    if (outcome.Feedback.Count > 0) await runs.AddIssuesAsync(run.Id, outcome.Feedback.Select(f => f.Issue).ToList(), IssueCap, ct);
                    stop = await runs.AdvanceAsync(run.Id, new ImportChunkResult(result.Cursor, chunk.Rows.Count, outcome.Inserted, outcome.Updated,
                        outcome.Skipped, outcome.Errored, outcome.Unchanged), progress, ct);
                }
                else
                {
                    // Held back: if the check passes these rows are found again by the real pass and recorded then.
                    foreach (var f in outcome.Feedback.TakeWhile(_ => result.BufferedIssues.Count < IssueCap)) result.BufferedIssues.Add(f.Issue);
                    stop = await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), progress, ct);
                }
                if (stop)
                {
                    result.Cancelled = true;
                    break;
                }
            }
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    /// <summary>Ends a run whose pre-check found rejected rows under "abort if any issue": nothing was written.</summary>
    private async Task RejectRunAsync(ImportPlan plan, ImportRun run, ImportRunSnapshot? snapshot, PassResult check, CancellationToken ct)
    {
        await runs.AddIssuesAsync(run.Id, check.BufferedIssues, IssueCap, ct);
        await runs.AdvanceAsync(run.Id, new ImportChunkResult(check.Cursor, check.RowsRead, 0, 0, check.Skipped, check.Errored), 100, ct);
        var detail = $"Nothing was imported. {check.Errored:N0} of {check.RowsRead:N0} rows have problems, and this import is set to stop when any row does. " +
                     "The details file lists them.";
        var (path, note) = await SaveDetailsAsync(check.Details, run, ct);
        await EndAsync(run, snapshot, plan, ImportRunStatus.Failed, note is null ? detail : $"{detail} {note}", path, ct);
    }

    private async Task CompleteRunAsync(ImportPlan plan, ImportRun run, ImportRunSnapshot? snapshot, PassResult final, CancellationToken ct)
    {
        if (final.RowsRead != final.Imported + final.Unchanged + final.Skipped + final.Errored)
            logger.LogError("Import run {RunId} does not reconcile: read {Read}, imported {Imported}, unchanged {Unchanged}, skipped {Skipped}, errored {Errored}.",
                run.PublicId, final.RowsRead, final.Imported, final.Unchanged, final.Skipped, final.Errored);

        var (path, note) = await SaveDetailsAsync(final.Details, run, ct);
        if (final.Cancelled)
        {
            var detail = final.Imported == 0
                ? "Cancelled before anything was imported."
                : $"Cancelled. {final.Imported:N0} rows were imported before it stopped and stay in the table.";
            await EndAsync(run, snapshot, plan, ImportRunStatus.Cancelled, note is null ? detail : $"{detail} {note}", path, ct);
            return;
        }

        // Rows left out by a rule are not failures; the run is "partial" whenever any row was not imported.
        var status = final.Errored > 0 && final.Imported + final.Unchanged == 0 ? ImportRunStatus.Failed
            : final.Errored + final.Skipped > 0 ? ImportRunStatus.Partial
            : ImportRunStatus.Success;
        await EndAsync(run, snapshot, plan, status, note, path, ct);
    }

    /// <summary>The single way a run ends: record the outcome, audit it, and tell the people involved. The counters are read back
    /// from the run's own record, so the email and the audit entry always agree with the run page.</summary>
    private async Task EndAsync(
        ImportRun run, ImportRunSnapshot? snapshot, ImportPlan? plan, string status, string? detail, string? feedbackPath, CancellationToken ct)
    {
        await runs.CompleteAsync(run.Id, status, detail, feedbackPath, ct);
        // The uploaded file is kept, so the history can show what was imported from, until the retention ends; then the clean-up deletes it.
        if (snapshot?.File is not null)
        {
            try { await fileAccess.RetainAsync(snapshot, DateTime.UtcNow.AddDays(options.Value.EffectiveRetentionDays), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not keep the uploaded file of import run {RunId}; the day-old clean-up may remove it.", run.PublicId);
            }
        }
        var finished = await runs.GetByPublicIdAsync(run.PublicId, ct) ?? run;
        if (plan is not null)
            await audit.LogActivityAsync(status switch
            {
                ImportRunStatus.Success or ImportRunStatus.Partial => "ImportCompleted",
                ImportRunStatus.Cancelled => "ImportCancelled",
                _ => "ImportFailed"
            }, "ImportRun", run.PublicId.ToString(), null, plan.Destination.AppId, null,
                $"{{\"read\":{finished.RowsRead},\"inserted\":{finished.Inserted},\"updated\":{finished.Updated},\"skipped\":{finished.Skipped},\"errored\":{finished.Errored}}}", ct);
        if (snapshot is null) return;
        try
        {
            // PRAI: Import Email Functionality Commented Temporary
            //if (await notifier.NotifyAsync(finished, snapshot, ct) is { Length: > 0 } mailNote) await runs.AppendDetailAsync(run.Id, mailNote, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The notifier is built never to throw; this keeps the guarantee even if a replacement does. The run is already recorded.
            logger.LogWarning(ex, "Could not notify about import run {RunId}.", run.PublicId);
        }
    }

    /// <summary>Uploads the details file (every run has one, even when nothing was imported). A storage problem must not undo an import that
    /// has otherwise finished, so it is reported on the run instead of failing it.</summary>
    private async Task<(string? Path, string? Note)> SaveDetailsAsync(ImportDetailsWriter details, ImportRun run, CancellationToken ct)
    {
        try { return (await details.SaveAsync(storage, run.PublicId, 0, ct), null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not save the details file for import run {RunId}.", run.PublicId);
            return (null, "The details file could not be saved; the rows that were not imported are listed on this page.");
        }
    }

    /// <summary>The details file of one table: its headings are the fields the rows were mapped into.</summary>
    internal static ImportDetailsWriter NewDetailsWriter(ImportPlan plan) => new(
        plan.Mappings.Select(m => ImportTypeCompatibility.DisplayName(m.Destination)).ToList(),
        plan.DestinationFields.Where(f => f.Fid.HasValue).GroupBy(f => f.Fid!.Value)
            .ToDictionary(g => g.Key, g => ImportTypeCompatibility.DisplayName(g.First())));

    /// <summary>Record IDs only grow, so the cursor's place in (0, maxId] is a cheap, count-free progress estimate,
    /// scaled into the part of the run this pass covers.</summary>
    private static byte Progress(int from, int to, long cursor, long maxId) =>
        maxId <= 0 ? (byte)from : (byte)Math.Min(to - 1, from + cursor * (to - from) / maxId);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
