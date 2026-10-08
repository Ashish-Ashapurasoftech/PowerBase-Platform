using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

/// <summary>Runs an import that fills several tables from the same source in one run. The source is read once, a chunk at a time; each chunk
/// goes to every table's own writer (its own mapping, import type, merge key and column rules), so a row is checked and written
/// independently for each table: fine for one table and rejected for another is imported into the first and reported for the second.
/// Counts are kept per table, the run's own counters are their totals, and one details file names the table of every line.
/// An import into one table never comes here: <see cref="ImportRunProcessor"/> keeps its own, unchanged path for it.</summary>
public sealed class ImportMultiTargetRunner(
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
    IImportFileAccess fileAccess,
    ILogger<ImportMultiTargetRunner> logger)
{
    private const int IssueCap = 5000;

    /// <summary>The single way the processor ends a run (record, audit, tell people, delete the uploaded file); handed in so both paths end alike.</summary>
    public delegate Task EndRun(ImportRun run, ImportRunSnapshot? snapshot, ImportPlan? plan, string status, string? detail, string? feedbackPath, CancellationToken ct);

    private sealed class Target(int index, ImportPlan plan)
    {
        public int Index { get; } = index;
        public ImportPlan Plan { get; } = plan;
        public string Label { get; } = plan.Destination.Name;
        public ImportDataRuleGate? Gate { get; set; }
    }

    /// <summary>What one table got in a pass.</summary>
    private sealed class Tally
    {
        public long Inserted, Updated, Skipped, Errored;
        public long Imported => Inserted + Updated;
    }

    private sealed class Pass(ImportFeedbackWriter feedback, int targets) : IAsyncDisposable
    {
        public ImportFeedbackWriter Feedback { get; } = feedback;
        public Tally[] Tallies { get; } = Enumerable.Range(0, targets).Select(_ => new Tally()).ToArray();
        public List<ImportRunIssue> BufferedIssues { get; } = new();
        public long RowsRead, Cursor;
        public bool Cancelled { get; set; }
        public long Inserted => Tallies.Sum(t => t.Inserted);
        public long Updated => Tallies.Sum(t => t.Updated);
        public long Skipped => Tallies.Sum(t => t.Skipped);
        public long Errored => Tallies.Sum(t => t.Errored);
        public long Imported => Inserted + Updated;
        public ValueTask DisposeAsync() => Feedback.DisposeAsync();
    }

    public async Task RunAsync(ImportRun run, ImportRunSnapshot snapshot, EndRun end, CancellationToken ct)
    {
        IReadOnlyList<ImportPlan> plans;
        try
        {
            var file = snapshot.File is null ? null : await fileAccess.OpenSourceAsync(snapshot, countRows: true, ct);
            plans = await planBuilder.BuildAllAsync(snapshot.Config, snapshot.DestinationTableId, ct, file);
            foreach (var plan in plans) await planBuilder.EnsureMergeKeyIsUniqueAsync(plan, ct);
            if (plans[0].File is { DataRows: 0 }) throw new ImportFileFormatException("The file has no rows to import below its column names.");
        }
        catch (Exception ex) when (ex is DomainException or ImportFileFormatException or FileNotFoundException or DirectoryNotFoundException)
        {
            var message = ex is FileNotFoundException or DirectoryNotFoundException ? "The uploaded file is no longer available. Upload it again." : ex.Message;
            await end(run, snapshot, null, ImportRunStatus.Failed, Truncate(message, 1000), null, ct);
            return;
        }

        var home = plans[0];
        var targets = plans.Select((p, i) => new Target(i, p)).ToList();
        var readPlan = ImportPlanBuilder.UnionForReading(plans);
        var maxId = run.SourceMaxId ?? readPlan.File?.LastRow ?? await store.GetMaxRecordIdAsync(readPlan.Source, ct);
        // False when the run was cancelled while it waited in the queue: it must stay cancelled.
        if (!await runs.MarkRunningAsync(run.Id, maxId, ct)) return;
        await audit.LogActivityAsync("ImportStarted", "ImportRun", run.PublicId.ToString(), null, home.Destination.AppId, ct: ct);

        try
        {
            await runs.InitTargetsAsync(run.Id, plans.Select(p => p.Destination.Id).ToList(), ct);
            foreach (var t in targets)
                t.Gate = await ImportDataRuleGate.CreateAsync(t.Plan.Destination, t.Plan.DestinationFields, tables, fieldRepo, records, formulaEngine, ct);

            List<Dictionary<int, HashSet<ulong>>?>? groups = null;
            var writeFrom = 0;
            var policy = home.ConstraintPolicy;
            if (policy == ImportConstraintPolicy.ExcludeDuplicateGroups)
            {
                groups = await ScanDuplicateGroupsAsync(targets, readPlan, run, maxId, 0, 45, ct);
                if (groups is null)
                {
                    await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, end, ct);
                    return;
                }
                writeFrom = 45;
            }
            else if (policy == ImportConstraintPolicy.AbortIfAnyIssue)
            {
                await using var check = await ExecutePassAsync(targets, readPlan, run, maxId, ImportPassMode.Validate, null, 0, 50, ct);
                if (check.Cancelled)
                {
                    await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, end, ct);
                    return;
                }
                if (check.Errored > 0)
                {
                    await RejectRunAsync(targets, run, snapshot, home, check, end, ct);
                    return;
                }
                writeFrom = 50;
            }

            await using var final = await ExecutePassAsync(targets, readPlan, run, maxId, ImportPassMode.Write, groups, writeFrom, 100, ct);
            await CompleteRunAsync(targets, run, snapshot, home, final, end, ct);
        }
        catch (OperationCanceledException) { throw; } // shutdown: the lease expires and the run is failed as interrupted on pickup
        catch (Exception ex)
        {
            logger.LogError(ex, "Import run {RunId} failed.", run.PublicId);
            await FinishAsync(run, snapshot, home, ImportRunStatus.Failed, Truncate(ex.Message, 1000), null, end, CancellationToken.None);
        }
    }

    private async Task<IImportChunkReader> OpenSourceAsync(ImportPlan readPlan, CancellationToken ct) =>
        readPlan.File is null ? reader : await fileAccess.OpenChunkReaderAsync(readPlan, ct);

    /// <summary>Reads the source once and, for every table, learns which unique values occur more than once, so the writing pass can leave
    /// out each such group entirely. Writes nothing. Null when the run was stopped.</summary>
    private async Task<List<Dictionary<int, HashSet<ulong>>?>?> ScanDuplicateGroupsAsync(
        List<Target> targets, ImportPlan readPlan, ImportRun run, long maxId, int progressFrom, int progressTo, CancellationToken ct)
    {
        var trackers = targets.Select(_ => new ImportDuplicateTracker()).ToList();
        var writers = targets.Select((t, i) => new ImportChunkWriter(t.Plan, store, records, null, formulaEngine, run.TriggeredByUserId,
            ImportPassMode.Validate, trackers[i], t.Label)).ToList();
        await using var source = await OpenSourceAsync(readPlan, ct);
        long cursor = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await source.ReadChunkAsync(readPlan, cursor, maxId, ct);
            if (chunk.Exhausted) break;
            foreach (var writer in writers) writer.CollectDuplicateGroups(chunk.Rows);
            cursor = chunk.LastId;
            if (await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), Progress(progressFrom, progressTo, cursor, maxId), ct))
                return null;
        }
        return trackers.Select(t => t.DuplicateGroups).ToList();
    }

    /// <summary>One pass over the source: read a chunk, hand it to every table's writer, record what was rejected, and advance the run. A table's
    /// counts and record total are recorded as soon as its part of the chunk is written, so a failure in a later table never leaves an
    /// earlier table's rows uncounted. A validate pass does everything except the writes.</summary>
    private async Task<Pass> ExecutePassAsync(
        List<Target> targets, ImportPlan readPlan, ImportRun run, long maxId, ImportPassMode mode,
        List<Dictionary<int, HashSet<ulong>>?>? groups, int progressFrom, int progressTo, CancellationToken ct)
    {
        var writers = targets.Select((t, i) => new ImportChunkWriter(t.Plan, store, records, t.Gate, formulaEngine, run.TriggeredByUserId, mode,
            new ImportDuplicateTracker(groups?[i]), t.Label)).ToList();
        foreach (var writer in writers) await writer.PrepareAsync(ct);
        var pass = new Pass(new ImportFeedbackWriter(["Values"], new Dictionary<int, string>(), includeTable: true), targets.Count);
        await using var source = await OpenSourceAsync(readPlan, ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await source.ReadChunkAsync(readPlan, pass.Cursor, maxId, ct);
                if (chunk.Exhausted) break;
                pass.Cursor = chunk.LastId;
                pass.RowsRead += chunk.Rows.Count;
                long chunkInserted = 0, chunkUpdated = 0, chunkSkipped = 0, chunkErrored = 0;

                for (var i = 0; i < targets.Count; i++)
                {
                    var outcome = chunk.Rows.Count == 0 ? ImportChunkOutcome.None : await writers[i].ProcessAsync(chunk.Rows, ct);
                    var tally = pass.Tallies[i];
                    tally.Inserted += outcome.Inserted; tally.Updated += outcome.Updated; tally.Skipped += outcome.Skipped; tally.Errored += outcome.Errored;
                    chunkInserted += outcome.Inserted; chunkUpdated += outcome.Updated; chunkSkipped += outcome.Skipped; chunkErrored += outcome.Errored;
                    await pass.Feedback.AppendAsync(outcome.Feedback, ct);

                    if (mode == ImportPassMode.Write)
                    {
                        if (outcome.Inserted > 0) await store.AddRecordCountAsync(targets[i].Plan.Destination.Id, (int)outcome.Inserted, ct);
                        if (outcome.Feedback.Count > 0) await runs.AddIssuesAsync(run.Id, outcome.Feedback.Select(f => f.Issue).ToList(), IssueCap, ct);
                        await runs.AddTargetCountsAsync(run.Id, [new ImportTargetCounts((byte)i, outcome.Inserted, outcome.Updated, outcome.Skipped, outcome.Errored)], ct);
                    }
                    else
                    {
                        // Held back: if the check passes these rows are found again by the real pass and recorded then.
                        foreach (var f in outcome.Feedback.TakeWhile(_ => pass.BufferedIssues.Count < IssueCap)) pass.BufferedIssues.Add(f.Issue);
                    }
                }

                var progress = Progress(progressFrom, progressTo, pass.Cursor, maxId);
                var stop = mode == ImportPassMode.Write
                    ? await runs.AdvanceAsync(run.Id, new ImportChunkResult(pass.Cursor, chunk.Rows.Count, chunkInserted, chunkUpdated, chunkSkipped, chunkErrored), progress, ct)
                    : await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), progress, ct);
                if (stop)
                {
                    pass.Cancelled = true;
                    break;
                }
            }
            return pass;
        }
        catch
        {
            await pass.DisposeAsync();
            throw;
        }
    }

    /// <summary>Ends a run whose pre-check found rejected rows under "abort if any issue": nothing was written, in any table.</summary>
    private async Task RejectRunAsync(List<Target> targets, ImportRun run, ImportRunSnapshot snapshot, ImportPlan home, Pass check, EndRun end, CancellationToken ct)
    {
        await runs.AddIssuesAsync(run.Id, check.BufferedIssues, IssueCap, ct);
        for (var i = 0; i < targets.Count; i++)
        {
            var t = check.Tallies[i];
            await runs.AddTargetCountsAsync(run.Id, [new ImportTargetCounts((byte)i, 0, 0, t.Skipped, t.Errored)], ct);
        }
        await runs.AdvanceAsync(run.Id, new ImportChunkResult(check.Cursor, check.RowsRead, 0, 0, check.Skipped, check.Errored), 100, ct);
        var detail = $"Nothing was imported. {check.Errored:N0} rows have problems, and this import is set to stop when any row does. The details file lists them.";
        var (path, note) = await SaveFeedbackAsync(check.Feedback, run, ct);
        await FinishAsync(run, snapshot, home, ImportRunStatus.Failed, note is null ? detail : $"{detail} {note}", path, end, ct);
    }

    private async Task CompleteRunAsync(List<Target> targets, ImportRun run, ImportRunSnapshot snapshot, ImportPlan home, Pass final, EndRun end, CancellationToken ct)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            var t = final.Tallies[i];
            if (final.RowsRead != t.Imported + t.Skipped + t.Errored)
                logger.LogError("Import run {RunId} does not reconcile for table {Table}: read {Read}, imported {Imported}, skipped {Skipped}, errored {Errored}.",
                    run.PublicId, targets[i].Label, final.RowsRead, t.Imported, t.Skipped, t.Errored);
        }

        var (path, note) = await SaveFeedbackAsync(final.Feedback, run, ct);
        if (final.Cancelled)
        {
            var detail = final.Imported == 0
                ? "Cancelled before anything was imported."
                : $"Cancelled. {final.Imported:N0} rows were imported before it stopped and stay in the tables.";
            await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, note is null ? detail : $"{detail} {note}", path, end, ct);
            return;
        }

        // Rows left out by a rule are not failures; the run is "partial" whenever any row was not imported into some table.
        var status = final.Errored > 0 && final.Imported == 0 ? ImportRunStatus.Failed
            : final.Errored + final.Skipped > 0 ? ImportRunStatus.Partial
            : ImportRunStatus.Success;
        await FinishAsync(run, snapshot, home, status, note, path, end, ct);
    }

    /// <summary>Makes the run's own counters the totals of the tables' (so they agree however the run ended), then ends it.</summary>
    private async Task FinishAsync(ImportRun run, ImportRunSnapshot? snapshot, ImportPlan plan, string status, string? detail, string? feedbackPath, EndRun end, CancellationToken ct)
    {
        try { await runs.SyncTotalsFromTargetsAsync(run.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not total the tables' counts for import run {RunId}.", run.PublicId); }
        await end(run, snapshot, plan, status, detail, feedbackPath, ct);
    }

    private async Task<(string? Path, string? Note)> SaveFeedbackAsync(ImportFeedbackWriter feedback, ImportRun run, CancellationToken ct)
    {
        try { return (await feedback.SaveAsync(storage, run.PublicId, ct), null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not save the feedback file for import run {RunId}.", run.PublicId);
            return (null, "The details file could not be saved; the rows that were not imported are listed on this page.");
        }
    }

    private static byte Progress(int from, int to, long cursor, long maxId) =>
        maxId <= 0 ? (byte)from : (byte)Math.Min(to - 1, from + cursor * (to - from) / maxId);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
