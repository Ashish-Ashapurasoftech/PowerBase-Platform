using Microsoft.Extensions.Logging;
using PowerBase.Application.QbMigration;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Final sync of one loaded table: copies again the records changed in QuickBase since the load (or the last sync)
/// and marks deleted the records QuickBase no longer has. QuickBase is the source: a PowerBase edit to a record that also
/// changed in QuickBase is overwritten. Records created above the planned ranges are left to plan --extend + load.</summary>
public sealed class QbmSyncer(IQuickBaseClient client, QbmChunkLoader loader, QbmStateStore store, QbmPowerBaseReader pb, ILogger logger)
{
    private readonly QbUserParentIndexes _userParents = new(client);
    private readonly QbmParentKeyCache _parentKeys = new(pb.ParentKeyMapAsync);

    /// <summary>Records written per transaction.</summary>
    public const int BatchSize = 2_000;

    public sealed record Options(bool DryRun, bool Deletes, int MaxDeletePercent, DateTime? SinceUtc);

    /// <param name="Changed">Records copied again (or that would be, in a dry run).</param>
    /// <param name="Deleted">Records marked deleted; null when deletes weren't checked.</param>
    /// <param name="Blocked">Why deletes were refused (too many for the safety limit), or null.</param>
    public sealed record Result(int Changed, int? Deleted, string? Blocked);

    /// <param name="plannedEnd">End (exclusive) of the table's planned Record ID ranges; ids at or above it are new records.</param>
    /// <summary>Where the sync of a table starts reading: the given time, else the last finished sync, else the load.</summary>
    public async Task<DateTime> SinceAsync(QbmTableContext ctx, Options options, CancellationToken ct = default)
    {
        var since = options.SinceUtc ?? await store.SyncStartAsync(ctx.TableMapId, ct)
            ?? throw new InvalidOperationException($"{ctx.PbTable.Name}: never loaded; nothing to sync.");
        // Clocks differ a little between QuickBase and the database; reading a few minutes twice costs nothing.
        return since.AddMinutes(-10);
    }

    /// <summary>Records changed in PowerBase since the load or the last sync, which the sync would overwrite (see <see cref="QbSyncConflicts"/>).</summary>
    /// <param name="activity">The app's activity log since the earliest start of any table.</param>
    public async Task<List<QbConflict>> ConflictsAsync(QbmTableContext ctx, Options options, IReadOnlyList<QbmStateStore.AuditEntry> activity, CancellationToken ct = default)
    {
        var since = await SinceAsync(ctx, options, ct);
        var rows = await store.RecentRowsAsync(ctx.PbTable.Id, since, ct);
        var audited = activity.Where(a => a.EntityType == "Record" && a.Action != "Deleted" && a.OccurredOn >= since && Guid.TryParse(a.EntityId, out _))
            .Select(a => Guid.Parse(a.EntityId!)).ToHashSet();
        // Only the rows that could be a conflict are looked up in QuickBase: deleted ones, and live migrated ones changed since.
        var ask = rows.Where(r => r.PublicId == QbmChunkLoader.StablePublicId(ctx.PbTable.Id, r.Id)).Select(r => r.Id).ToList();
        var qbModified = ask.Count == 0 ? [] : await new QbKeysetReader(client).ModifiedOnAsync(ctx.QbTableId, ask, ct);
        return QbSyncConflicts.Find(rows, id => QbmChunkLoader.StablePublicId(ctx.PbTable.Id, id), qbModified, audited, since);
    }

    /// <param name="recopy">Record IDs to copy from QuickBase even if QuickBase didn't change them (records edited or deleted in
    /// PowerBase that the operator chose to overwrite).</param>
    public async Task<Result> SyncTableAsync(QbmTableContext ctx, long plannedEnd, Options options, CancellationToken ct = default,
        IReadOnlyCollection<long>? recopy = null)
    {
        var since = await SinceAsync(ctx, options, ct);
        var syncId = await store.StartSyncAsync(ctx.RunId, ctx.TableMapId, since, options.DryRun, ct);
        try
        {
            var changed = await ChangesAsync(ctx, plannedEnd, since, options.DryRun, recopy ?? [], ct);
            int? deleted = null;
            string? blocked = null;
            if (options.Deletes) (deleted, blocked) = await DeletesAsync(ctx, options, ct);
            await store.FinishSyncAsync(syncId, changed, deleted, blocked, CancellationToken.None);
            return new Result(changed, deleted, blocked);
        }
        catch (Exception ex)
        {
            await store.FinishSyncAsync(syncId, 0, null, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<int> ChangesAsync(QbmTableContext ctx, long plannedEnd, DateTime since, bool dryRun, IReadOnlyCollection<long> recopy, CancellationToken ct)
    {
        var reader = new QbKeysetReader(client);
        var select = ctx.SelectFids.Contains(QbSystemFids.DateModified) ? ctx.SelectFids : [QbSystemFids.DateModified, .. ctx.SelectFids];
        var batch = new List<QbRow>();
        var count = 0;
        await foreach (var page in reader.ReadAsync(ctx.QbTableId, select, QbKeysetReader.ModifiedFilter(since), ct))
        {
            batch.AddRange(page.Where(r => r.RecordId < plannedEnd && QbKeysetReader.ModifiedSince(r, since)));
            while (batch.Count >= BatchSize)
            {
                count += await WriteAsync(ctx, batch[..BatchSize], dryRun, ct);
                batch.RemoveRange(0, BatchSize);
            }
        }
        count += await WriteAsync(ctx, batch, dryRun, ct);

        // Overwritten PowerBase changes: read those records by id (100 per request) and copy them as QuickBase has them.
        var copied = new HashSet<long>();
        foreach (var part in recopy.Where(id => id < plannedEnd).Distinct().Chunk(100))
        {
            var where = string.Join("OR", part.Select(id => $"{{{QbSystemFids.RecordId}.EX.{id}}}"));
            var page = await client.QueryAsync(ctx.QbTableId, select, where, 0, null, ct);
            var rows = page.Rows.Where(r => copied.Add(r.RecordId)).ToList();
            if (rows.Count > 0) logger.LogInformation("{Table}: {Count} records changed in PowerBase get QuickBase's values back", ctx.PbTable.Name, rows.Count);
            count += await WriteAsync(ctx, rows, dryRun, ct);
        }
        return count;
    }

    private async Task<int> WriteAsync(QbmTableContext ctx, List<QbRow> rows, bool dryRun, CancellationToken ct)
    {
        if (rows.Count == 0 || dryRun) return rows.Count;
        var lookups = await new QbmLookupLoader(pb, _userParents, _parentKeys).LoadAsync(ctx, rows, ct);
        var transformer = new QbValueTransformer(ctx.Mappings, lookups, ctx.Options, ctx.ReferencesByKey.Keys.ToList());
        var issues = new List<QbIssue>();
        var transformed = rows.Select(r => transformer.Transform(r, issues)).ToList();
        var written = await loader.UpsertAsync(ctx.TenantId, ctx.PbTable, ctx.PbFields, transformed, ct);
        await store.AddIssuesAsync(ctx.RunId, ctx.TableMapId, null, issues, ct);
        logger.LogInformation("{Table}: {Count} changed records copied again (ids {From}..{To})",
            ctx.PbTable.Name, written, rows[0].RecordId, rows[^1].RecordId);
        return written;
    }

    private async Task<(int? Deleted, string? Blocked)> DeletesAsync(QbmTableContext ctx, Options options, CancellationToken ct)
    {
        var qbIds = await new QbKeysetReader(client).RecordIdsAsync(ctx.QbTableId, ct);
        var pbIds = await store.LiveIdsAsync(ctx.PbTable.Id, ct);
        var gone = QbKeysetReader.Missing(qbIds, pbIds);
        if (gone.Count == 0) return (0, null);

        // An empty or partial answer from QuickBase (token lost access, a role filter) must not wipe the table.
        var limit = Math.Max(100, pbIds.Count * (long)options.MaxDeletePercent / 100);
        if (gone.Count > limit)
        {
            var why = $"{gone.Count} of {pbIds.Count} records are not in QuickBase, more than the safety limit ({limit}); nothing was deleted. " +
                      "Check the QuickBase token can see every record, then run again with --max-delete-percent.";
            logger.LogError("{Table}: {Why}", ctx.PbTable.Name, why);
            return (null, why);
        }
        if (options.DryRun)
        {
            logger.LogInformation("{Table}: {Count} records would be marked deleted, e.g. ids {Ids}", ctx.PbTable.Name, gone.Count, string.Join(", ", gone.Take(20)));
            return (gone.Count, null);
        }
        var deleted = 0;
        foreach (var part in gone.Chunk(5_000)) deleted += await loader.MarkDeletedAsync(ctx.PbTable, part, ct);
        logger.LogInformation("{Table}: {Count} records deleted in QuickBase marked deleted", ctx.PbTable.Name, deleted);
        return (deleted, null);
    }
}
