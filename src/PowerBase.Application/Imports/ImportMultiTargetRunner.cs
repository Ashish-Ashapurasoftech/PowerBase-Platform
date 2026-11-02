using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Imports;

/// <summary>Runs an import that fills several tables from the same source in one run, or that reads a workbook sheet by sheet. The source is read
/// once, a chunk at a time; each chunk goes to every plan's own writer (its own mapping, import type, merge key and column rules), so a row is
/// checked and written independently for each table: fine for one table and rejected for another is imported into the first and reported for the
/// second. A table that reads sheet by sheet has a plan for each sheet; each sheet is a **source group** (read once, giving its rows to every plan
/// that uses it), and the groups are run one after the other. Counts and the details file are kept per table, whatever sheets it took, and the
/// run's own counters are their totals. An import into one table from one source never comes here: <see cref="ImportRunProcessor"/> keeps its own,
/// unchanged path for it.</summary>
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
    ILogger<ImportMultiTargetRunner> logger,
    IReferenceOriginalFields? originals = null,
    PowerBase.Application.Records.ISnapshotCaptureService? snapshots = null)
{
    private const int IssueCap = 5000;

    /// <summary>The single way the processor ends a run (record, audit, tell people, delete the uploaded file); handed in so both paths end alike.</summary>
    public delegate Task EndRun(ImportRun run, ImportRunSnapshot? snapshot, ImportPlan? plan, string status, string? detail, string? feedbackPath, CancellationToken ct);

    /// <summary>One plan: a table, as read from one source (for a table read sheet by sheet, one of its sheets).</summary>
    private sealed class Target(int index, ImportPlan plan)
    {
        public int Index { get; } = index;
        public int TableIndex { get; } = plan.TableIndex;
        public ImportPlan Plan { get; } = plan;
        public string Label { get; } = plan.Destination.Name;
        public ImportDataRuleGate? Gate { get; set; }
        /// <summary>Where this plan's values go among its table's details file's value columns (null: they are in the file's order).</summary>
        public int[]? ValueAt { get; set; }
    }

    /// <summary>One thing read once: the whole file or table, or one sheet of a workbook, and the plans that take their rows from it.</summary>
    private sealed class SourceGroup(ImportPlan readPlan, List<Target> targets, long maxId, bool virtualsPerPlan)
    {
        public ImportPlan ReadPlan { get; } = readPlan;
        public List<Target> Targets { get; } = targets;
        public long MaxId { get; } = maxId;
        /// <summary>The plans calculate their own virtual columns (they were mapped separately, so they differ) instead of the read doing it once.</summary>
        public bool VirtualsPerPlan { get; } = virtualsPerPlan;
    }

    /// <summary>What one table got in a pass.</summary>
    private sealed class Tally
    {
        public long Inserted, Updated, Unchanged, Skipped, Errored;
        /// <summary>The source rows this table was given: all of them, or those its own filter let through.</summary>
        public long Matched;
        public long Imported => Inserted + Updated;
    }

    private sealed class Pass(ImportDetailsWriter[] details) : IAsyncDisposable
    {
        /// <summary>One details file per table, in the order of the tables.</summary>
        public ImportDetailsWriter[] Details { get; } = details;
        public Tally[] Tallies { get; } = Enumerable.Range(0, details.Length).Select(_ => new Tally()).ToArray();
        public List<ImportRunIssue> BufferedIssues { get; } = new();
        public long RowsRead, Cursor;
        public bool Cancelled { get; set; }
        public long Inserted => Tallies.Sum(t => t.Inserted);
        public long Updated => Tallies.Sum(t => t.Updated);
        public long Unchanged => Tallies.Sum(t => t.Unchanged);
        public long Skipped => Tallies.Sum(t => t.Skipped);
        public long Errored => Tallies.Sum(t => t.Errored);
        public long Imported => Inserted + Updated;
        public async ValueTask DisposeAsync() { foreach (var d in Details) await d.DisposeAsync(); }
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
        // One representative plan per table (the first), for its name, destination and rule gate; the counts and the details file are per table.
        var tableCount = targets.Max(t => t.TableIndex) + 1;
        var tableOf = Enumerable.Range(0, tableCount).Select(i => targets.First(t => t.TableIndex == i)).ToList();
        foreach (var table in tableOf)
        {
            var own = targets.Where(t => t.TableIndex == table.TableIndex).ToList();
            var (_, valueAt) = ImportRunProcessor.NewDetailsWriter(own.Select(t => t.Plan).ToList());
            for (var i = 0; i < own.Count; i++) own[i].ValueAt = valueAt[i];
        }

        var sourceGroups = new List<SourceGroup>();
        // A table that combines its sheets is read as one source of its own (its sheets are read together); a table read sheet by sheet shares each sheet.
        foreach (var members in targets.GroupBy(t => t.Plan.JoinedFrom is not null ? $"\0join{t.TableIndex}" : t.Plan.SheetName ?? "").Select(g => g.ToList()))
        {
            var perPlan = members[0].Plan.SheetName is not null;
            var readPlan = ImportPlanBuilder.UnionForReading(members.Select(t => t.Plan).ToList(), virtualsPerPlan: perPlan);
            var pinned = sourceGroups.Count == 0 ? run.SourceMaxId : null;
            sourceGroups.Add(new SourceGroup(readPlan, members, pinned ?? readPlan.File?.LastRow ?? await store.GetMaxRecordIdAsync(readPlan.Source, ct), perPlan));
        }
        var maxId = sourceGroups[0].MaxId;
        // False when the run was cancelled while it waited in the queue: it must stay cancelled.
        if (!await runs.MarkRunningAsync(run.Id, maxId, ct)) return;
        await audit.LogActivityAsync("ImportStarted", "ImportRun", run.PublicId.ToString(), null, home.Destination.AppId, ct: ct);

        try
        {
            await runs.InitTargetsAsync(run.Id, tableOf.Select(t => t.Plan.Destination.Id).ToList(), ct);
            foreach (var table in tableOf)
            {
                var gate = await ImportDataRuleGate.CreateAsync(table.Plan.Destination, table.Plan.DestinationFields, tables, fieldRepo, records, formulaEngine, ct);
                foreach (var t in targets.Where(t => t.TableIndex == table.TableIndex)) t.Gate = gate;
            }

            // A filter (the import's, a table's or a sheet's) that excludes duplicates needs the values of its source counted first.
            if (sourceGroups.Any(g => g.ReadPlan.RowFilter is { CountsDuplicates: true } || g.Targets.Any(t => t.Plan.TableFilter is { CountsDuplicates: true }))
                && !await CountFilterDuplicatesAsync(sourceGroups, run, ct))
            {
                await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, end, ct);
                return;
            }

            List<Dictionary<int, HashSet<ulong>>?>? duplicateGroups = null;
            var writeFrom = 0;
            var policy = home.ConstraintPolicy;
            if (policy == ImportConstraintPolicy.ExcludeDuplicateGroups)
            {
                duplicateGroups = await ScanDuplicateGroupsAsync(sourceGroups, tableCount, run, 0, 45, ct);
                if (duplicateGroups is null)
                {
                    await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, end, ct);
                    return;
                }
                writeFrom = 45;
            }
            else if (policy == ImportConstraintPolicy.AbortIfAnyIssue)
            {
                await using var check = await ExecutePassAsync(sourceGroups, targets, tableOf, run, ImportPassMode.Validate, null, 0, 50, ct);
                if (check.Cancelled)
                {
                    await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, "Cancelled before anything was imported.", null, end, ct);
                    return;
                }
                if (check.Errored > 0)
                {
                    await RejectRunAsync(tableOf, run, snapshot, home, check, end, ct);
                    return;
                }
                writeFrom = 50;
            }

            await using var final = await ExecutePassAsync(sourceGroups, targets, tableOf, run, ImportPassMode.Write, duplicateGroups, writeFrom, 100, ct);
            await CompleteRunAsync(tableOf, run, snapshot, home, final, end, ct);
        }
        catch (OperationCanceledException) { throw; } // shutdown: the lease expires and the run is failed as interrupted on pickup
        catch (Exception ex)
        {
            logger.LogError(ex, "Import run {RunId} failed.", run.PublicId);
            await FinishAsync(run, snapshot, home, ImportRunStatus.Failed, Truncate(ex.Message, 1000), null, end, CancellationToken.None);
        }
    }

    private async Task<IImportChunkReader> OpenSourceAsync(SourceGroup group, CancellationToken ct)
    {
        var readPlan = group.ReadPlan;
        if (readPlan.JoinedFrom is { } sheets) return new ImportJoinedChunkReader(sheets, readPlan, fileAccess, formulaEngine);
        IImportChunkReader source = readPlan.File is null ? reader : await fileAccess.OpenChunkReaderAsync(readPlan, ct);
        return readPlan.Virtuals.Count == 0 ? source : new ImportVirtualColumnReader(source, readPlan, formulaEngine);
    }

    /// <summary>The part of the run's progress a source group covers: the groups share the pass's range evenly.</summary>
    private static (int From, int To) GroupRange(int from, int to, int index, int count) =>
        (from + (to - from) * index / count, from + (to - from) * (index + 1) / count);

    /// <summary>Reads every source group once so each "does not have a duplicate" condition (in the import's filter, a table's or a sheet's) knows how often every value
    /// occurs and which row had it first: a table's filter counts the rows that reach it, after the import's filter. Writes nothing. False when the run was stopped.</summary>
    private async Task<bool> CountFilterDuplicatesAsync(List<SourceGroup> groups, ImportRun run, CancellationToken ct)
    {
        var filters = groups.SelectMany(g => new[] { g.ReadPlan.RowFilter }.Concat(g.Targets.Select(t => t.Plan.TableFilter)))
            .Where(f => f is { CountsDuplicates: true }).Select(f => f!).Distinct().ToList();
        foreach (var f in filters) f.BeginCountingDuplicates();
        try
        {
            foreach (var group in groups)
            {
                var own = group.Targets.Select(t => t.Plan.TableFilter).Where(f => f is { CountsDuplicates: true }).Select(f => f!).ToList();
                if (group.ReadPlan.RowFilter is not { CountsDuplicates: true } && own.Count == 0) continue;
                await using var source = await OpenSourceAsync(group, ct);
                long cursor = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = await source.ReadChunkAsync(group.ReadPlan, cursor, group.MaxId, ct);
                    if (chunk.Exhausted) break;
                    foreach (var filter in own) foreach (var row in chunk.Rows) filter.Matches(row);
                    cursor = chunk.LastId;
                    if (await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), 0, ct)) return false;
                }
            }
            return true;
        }
        finally { foreach (var f in filters) f.FinishCountingDuplicates(); }
    }

    /// <summary>Reads every source group once and, for every table, learns which unique values occur more than once (across all the sheets the
    /// table reads), so the writing pass can leave out each such group entirely. Writes nothing. Null when the run was stopped.</summary>
    private async Task<List<Dictionary<int, HashSet<ulong>>?>?> ScanDuplicateGroupsAsync(
        List<SourceGroup> groups, int tableCount, ImportRun run, int progressFrom, int progressTo, CancellationToken ct)
    {
        var trackers = Enumerable.Range(0, tableCount).Select(_ => new ImportDuplicateTracker()).ToList();
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var (from, to) = GroupRange(progressFrom, progressTo, g, groups.Count);
            var writers = group.Targets.Select(t => new ImportChunkWriter(t.Plan, store, records, null, formulaEngine, run.TriggeredByUserId,
                ImportPassMode.Validate, trackers[t.TableIndex], t.Label, originals, snapshots)).ToList();
            await using var source = await OpenSourceAsync(group, ct);
            long cursor = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await source.ReadChunkAsync(group.ReadPlan, cursor, group.MaxId, ct);
                if (chunk.Exhausted) break;
                for (var i = 0; i < writers.Count; i++) writers[i].CollectDuplicateGroups(RowsFor(group, group.Targets[i], chunk.Rows));
                cursor = chunk.LastId;
                if (await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), Progress(from, to, cursor, group.MaxId), ct))
                    return null;
            }
        }
        return trackers.Select(t => t.DuplicateGroups).ToList();
    }

    /// <summary>One pass over the sources: for each source group in turn, read a chunk, hand it to every plan's writer, record what was rejected,
    /// and advance the run. A table's counts and record total are recorded as soon as its part of the chunk is written, so a failure in a later
    /// table never leaves an earlier table's rows uncounted. A validate pass does everything except the writes. The values a unique field or a
    /// "Remove duplicates" column has already seen are remembered per table across all of its sheets.</summary>
    private async Task<Pass> ExecutePassAsync(
        List<SourceGroup> groups, List<Target> targets, List<Target> tableOf, ImportRun run, ImportPassMode mode,
        List<Dictionary<int, HashSet<ulong>>?>? duplicateGroups, int progressFrom, int progressTo, CancellationToken ct)
    {
        var trackers = tableOf.Select((t, i) => new ImportDuplicateTracker(duplicateGroups?[i])).ToList();
        var pass = new Pass(tableOf.Select(t => ImportRunProcessor.NewDetailsWriter(targets.Where(x => x.TableIndex == t.TableIndex).Select(x => x.Plan).ToList()).Writer).ToArray());
        try
        {
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var (from, to) = GroupRange(progressFrom, progressTo, g, groups.Count);
                var writers = group.Targets.Select(t => new ImportChunkWriter(t.Plan, store, records, t.Gate, formulaEngine, run.TriggeredByUserId, mode,
                    trackers[t.TableIndex], t.Label, originals, snapshots)).ToList();
                foreach (var writer in writers) await writer.PrepareAsync(ct);
                await using var source = await OpenSourceAsync(group, ct);
                pass.Cursor = 0;
                // Sheets that are combined give records, but what was read is their rows: reported once, with the first chunk.
                var joined = source as ImportJoinedChunkReader;
                var sourceRowsCounted = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = await source.ReadChunkAsync(group.ReadPlan, pass.Cursor, group.MaxId, ct);
                    var rowsRead = joined is null ? chunk.Rows.Count : sourceRowsCounted ? 0 : joined.SourceRows;
                    sourceRowsCounted = true;
                    if (chunk.Exhausted) { pass.RowsRead += rowsRead; break; }
                    pass.Cursor = chunk.LastId;
                    pass.RowsRead += rowsRead;
                    long chunkInserted = 0, chunkUpdated = 0, chunkUnchanged = 0, chunkSkipped = 0, chunkErrored = 0;

                    for (var i = 0; i < group.Targets.Count; i++)
                    {
                        var target = group.Targets[i];
                        // A plan with its own filter (a table's, or a sheet's) is given only the rows that pass it; the others are neither imported,
                        // skipped nor reported for it.
                        var rows = RowsFor(group, target, chunk.Rows);
                        var outcome = rows.Count == 0 ? ImportChunkOutcome.None : await writers[i].ProcessAsync(rows, ct);
                        var tally = pass.Tallies[target.TableIndex];
                        tally.Matched += rows.Count;
                        tally.Inserted += outcome.Inserted; tally.Updated += outcome.Updated; tally.Unchanged += outcome.Unchanged;
                        tally.Skipped += outcome.Skipped; tally.Errored += outcome.Errored;
                        chunkInserted += outcome.Inserted; chunkUpdated += outcome.Updated; chunkUnchanged += outcome.Unchanged;
                        chunkSkipped += outcome.Skipped; chunkErrored += outcome.Errored;
                        await pass.Details[target.TableIndex].AppendAsync(outcome.Feedback, outcome.Written, ct, target.ValueAt, target.Plan.SheetName);

                        if (mode == ImportPassMode.Write)
                        {
                            if (outcome.Inserted > 0) await store.AddRecordCountAsync(target.Plan.Destination.Id, (int)outcome.Inserted, ct);
                            if (outcome.Feedback.Count > 0) await runs.AddIssuesAsync(run.Id, outcome.Feedback.Select(f => f.Issue).ToList(), IssueCap, ct);
                            await runs.AddTargetCountsAsync(run.Id,
                                [new ImportTargetCounts((byte)target.TableIndex, outcome.Inserted, outcome.Updated, outcome.Skipped, outcome.Errored, outcome.Unchanged)], ct);
                        }
                        else
                        {
                            // Held back: if the check passes these rows are found again by the real pass and recorded then.
                            foreach (var f in outcome.Feedback.TakeWhile(_ => pass.BufferedIssues.Count < IssueCap)) pass.BufferedIssues.Add(f.Issue);
                        }
                    }

                    var progress = Progress(from, to, pass.Cursor, group.MaxId);
                    var stop = mode == ImportPassMode.Write
                        ? await runs.AdvanceAsync(run.Id, new ImportChunkResult(pass.Cursor, rowsRead, chunkInserted, chunkUpdated, chunkSkipped, chunkErrored, chunkUnchanged), progress, ct)
                        : await runs.AdvanceAsync(run.Id, new ImportChunkResult(0, 0, 0, 0, 0, 0), progress, ct);
                    if (stop)
                    {
                        pass.Cancelled = true;
                        return pass;
                    }
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
    private async Task RejectRunAsync(List<Target> tableOf, ImportRun run, ImportRunSnapshot snapshot, ImportPlan home, Pass check, EndRun end, CancellationToken ct)
    {
        await runs.AddIssuesAsync(run.Id, check.BufferedIssues, IssueCap, ct);
        for (var i = 0; i < tableOf.Count; i++)
        {
            var t = check.Tallies[i];
            await runs.AddTargetCountsAsync(run.Id, [new ImportTargetCounts((byte)i, 0, 0, t.Skipped, t.Errored)], ct);
        }
        await runs.AdvanceAsync(run.Id, new ImportChunkResult(check.Cursor, check.RowsRead, 0, 0, check.Skipped, check.Errored), 100, ct);
        var detail = $"Nothing was imported. {check.Errored:N0} rows have problems, and this import is set to stop when any row does. The details file lists them.";
        var (path, note) = await SaveDetailsAsync(check, run, ct);
        await FinishAsync(run, snapshot, home, ImportRunStatus.Failed, note is null ? detail : $"{detail} {note}", path, end, ct);
    }

    private async Task CompleteRunAsync(List<Target> tableOf, ImportRun run, ImportRunSnapshot snapshot, ImportPlan home, Pass final, EndRun end, CancellationToken ct)
    {
        for (var i = 0; i < tableOf.Count; i++)
        {
            var t = final.Tallies[i];
            if (t.Matched != t.Imported + t.Unchanged + t.Skipped + t.Errored)
                logger.LogError("Import run {RunId} does not reconcile for table {Table}: matched {Matched} of {Read} rows, imported {Imported}, skipped {Skipped}, errored {Errored}.",
                    run.PublicId, tableOf[i].Label, t.Matched, final.RowsRead, t.Imported, t.Skipped, t.Errored);
        }

        var (path, note) = await SaveDetailsAsync(final, run, ct);
        if (final.Cancelled)
        {
            var detail = final.Imported == 0
                ? "Cancelled before anything was imported."
                : $"Cancelled. {final.Imported:N0} rows were imported before it stopped and stay in the tables.";
            await FinishAsync(run, snapshot, home, ImportRunStatus.Cancelled, note is null ? detail : $"{detail} {note}", path, end, ct);
            return;
        }

        // Rows left out by a rule are not failures; the run is "partial" whenever any row was not imported into some table.
        var status = final.Errored > 0 && final.Imported + final.Unchanged == 0 ? ImportRunStatus.Failed
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

    /// <summary>Uploads every table's details file (each run has one per table, even when nothing was imported into it) and records where each
    /// is kept. Returns the first table's path, which is the run's own file, and a note when a file could not be saved: a storage problem
    /// must not undo an import that has otherwise finished.</summary>
    private async Task<(string? Path, string? Note)> SaveDetailsAsync(Pass pass, ImportRun run, CancellationToken ct)
    {
        string? first = null;
        var failed = false;
        for (var i = 0; i < pass.Details.Length; i++)
        {
            try
            {
                var path = await pass.Details[i].SaveAsync(storage, run.PublicId, i, ct);
                if (i == 0) first = path;
                await runs.SetTargetDetailsAsync(run.Id, (byte)i, path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not save the details file of table {Table} for import run {RunId}.", i, run.PublicId);
                failed = true;
            }
        }
        return (first, failed ? "A details file could not be saved; the rows that were not imported are listed on this page." : null);
    }

    /// <summary>The rows of a chunk this plan is given: all of them, or those that pass its own filter (a table's, or for a sheet read on its
    /// own, that sheet's); then, when plans have virtual columns of their own, with this plan's virtual columns added.</summary>
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> RowsFor(SourceGroup group, Target target, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (target.Plan.TableFilter is { } filter)
        {
            var kept = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
            foreach (var row in rows) if (filter.Matches(row)) kept.Add(row);
            rows = kept;
        }
        return group.VirtualsPerPlan && rows.Count > 0 ? ImportVirtualColumnReader.Apply(target.Plan, formulaEngine, rows) : rows;
    }

    private static byte Progress(int from, int to, long cursor, long maxId) =>
        maxId <= 0 ? (byte)from : (byte)Math.Min(to - 1, from + cursor * (to - from) / maxId);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
