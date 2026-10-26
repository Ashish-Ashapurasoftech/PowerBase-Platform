using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables.Sync;

public enum ConnectedRunOutcome
{
    /// <summary>The run reached a final state (or there was nothing to do).</summary>
    Done,
    /// <summary>The run could not go ahead now (table busy, transient failure): the queue item is retried later.</summary>
    RetryLater,
}

/// <summary>Executes one queued refresh (docs/CONNECTED_TABLES_IMPLEMENTATION_PLAN.md §7):
/// gates → schema drift check → (refresh-key check) → chunked read of the source as the connected user → per record:
/// insert, update only the connected fields that differ (this also reverts edits made to the copy by other paths), or
/// leave alone → mirror phase behind the mass-delete guard → bookkeeping and notifications.
/// Deletions only ever happen after a complete, successful read; a cancelled or failed run removes nothing.</summary>
public sealed class ConnectedSyncProcessor(
    IConnectedRunRepository runs,
    IConnectedTableRepository connectedTables,
    IConnectedConnectionRepository connections,
    IConnectedControlRepository control,
    IConnectedDataWriter writer,
    IConnectedTableLock tableLock,
    ConnectedAccessVerifier verifier,
    IAppTableRepository appTables,
    IAppFieldRepository appFields,
    IUserRepository users,
    IAuditRepository audit,
    IQueryContext context,
    ConnectedNotifier notifier,
    IOptions<ConnectedTablesOptions> options,
    ILogger<ConnectedSyncProcessor> logger)
{
    private const int IssueCap = 5000;
    private const int DeleteBatch = 1000;
    private readonly ConnectedTablesOptions _options = options.Value;

    private sealed record ActiveField(ConnectedField Mapping, AppField Source, AppField Destination, ConnectedFieldPlan Plan);

    private sealed record Item(string Key, Dictionary<long, object?> Values, Dictionary<long, string?> Canonical, byte[] Hash);

    public async Task<ConnectedRunOutcome> RunAsync(Guid runPublicId, int attempt, CancellationToken ct)
    {
        var run = await runs.GetByPublicIdAsync(runPublicId, ct);
        if (run is null || ConnectedRunStatuses.IsFinished(run.Status)) return ConnectedRunOutcome.Done;

        var table = await connectedTables.GetByIdAsync(run.ConnectedTableId, ct);
        if (table is null)
        {
            await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, "The connected table no longer exists.", ct);
            return ConnectedRunOutcome.Done;
        }

        // A run left 'running' was interrupted (worker crash or restart). Upserts by key are idempotent, so it simply
        // starts over — a limited number of times.
        if (run.Status == ConnectedRunStatuses.Running)
        {
            if (attempt > _options.MaxRetries + 1)
            {
                await FailAsync(run, table, null, $"The refresh was interrupted after {run.RowsRead:N0} source records and could not be resumed. Start a new refresh.", gateFailure: false, ct);
                return ConnectedRunOutcome.Done;
            }
            await runs.ResetToQueuedAsync(run.Id, ct);
            run = await runs.GetByPublicIdAsync(runPublicId, ct);
            if (run is null || ConnectedRunStatuses.IsFinished(run.Status)) return ConnectedRunOutcome.Done;
        }

        await using var held = await tableLock.TryAcquireAsync(table.Id, ct);
        if (held is null) return ConnectedRunOutcome.RetryLater;

        var snapshot = JsonSerializer.Deserialize<ConnectedRunSnapshot>(run.SnapshotJson) ?? ConnectedRunSnapshot.Of(table);
        AppTable? destination = null;
        try
        {
            return await ExecuteAsync(run, table, snapshot, d => destination = d, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the run starts over when the queue lease runs out.
            await runs.ResetToQueuedAsync(run.Id, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (IsTransient(ex) && attempt <= _options.MaxRetries)
        {
            logger.LogWarning(ex, "Connected refresh {RunId} hit a transient error (attempt {Attempt}); it will be retried.", run.PublicId, attempt);
            await runs.ResetToQueuedAsync(run.Id, CancellationToken.None);
            return ConnectedRunOutcome.RetryLater;
        }
        catch (ConnectedTableException ex)
        {
            await FailAsync(run, table, destination, ex.Message, gateFailure: true, CancellationToken.None);
            return ConnectedRunOutcome.Done;
        }
        catch (DomainException ex)
        {
            await FailAsync(run, table, destination, ex.Message, gateFailure: false, CancellationToken.None);
            return ConnectedRunOutcome.Done;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connected refresh {RunId} failed.", run.PublicId);
            await FailAsync(run, table, destination, IsTransient(ex)
                ? "The source or destination database could not be reached. The refresh will run again at its next scheduled time."
                : "The refresh failed unexpectedly. Try again; if it keeps failing, contact support.", gateFailure: false, CancellationToken.None);
            return ConnectedRunOutcome.Done;
        }
    }

    private async Task<ConnectedRunOutcome> ExecuteAsync(ConnectedRun run, ConnectedTable table, ConnectedRunSnapshot snapshot, Action<AppTable> seen, CancellationToken ct)
    {
        var consumerTenantId = context.TenantId;

        // ── Destination still exists? ───────────────────────────────────────────────────────────────────────
        AppTable? destination;
        try { destination = await appTables.GetByIdAsync(table.AppTableId, ct); }
        catch (NotFoundException) { destination = null; }
        if (destination is null)
        {
            await connectedTables.SoftDeleteAsync(table.Id, ConnectedTableStatuses.Removed, ct);
            ConnectedTableCache.Invalidate(consumerTenantId);
            if (await control.GetLinkAsync(consumerTenantId, table.PublicId, ct) is { } link)
                await control.SetLinkStatusAsync(link.Id, ConnectedLinkStatuses.Removed, ct);
            await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, "The destination table was deleted.", ct);
            return ConnectedRunOutcome.Done;
        }
        seen(destination);

        // ── Gates (G2–G12) ──────────────────────────────────────────────────────────────────────────────────
        var connection = await connections.GetByIdAsync(table.ConnectionId, ct)
            ?? throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "The connection this table used was deleted. Switch the table to another connection.");
        // The provider can stop one table's access without revoking the whole realm.
        if (await control.GetLinkAsync(consumerTenantId, table.PublicId, ct) is { Status: ConnectedLinkStatuses.RevokedByProvider })
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.DataShareNotApproved, "The source realm stopped this table's access to its data.");
        await using var access = await verifier.OpenAsync(connection, consumerTenantId, ct);
        var source = await verifier.GetSourceTableAsync(access, table.SourceTablePublicId, ct);

        // ── Schema drift ────────────────────────────────────────────────────────────────────────────────────
        var issues = new List<ConnectedRunIssue>();
        var issueCount = 0;
        var partial = false;
        void Issue(string outcome, string code, string message, long? sourceRecordId = null, int? sourceFid = null)
        {
            if (issueCount++ >= IssueCap) return;
            issues.Add(new ConnectedRunIssue { RunId = run.Id, Outcome = outcome, ReasonCode = code, Message = message, SourceRecordId = sourceRecordId, SourceFid = sourceFid });
        }

        var destinationFields = (await appFields.ListByTableAsync(destination.Id, ct) ?? Array.Empty<AppField>()).Where(f => !f.IsDeleted).ToList();
        var destinationById = destinationFields.ToDictionary(f => f.Id);
        var active = new List<ActiveField>();
        foreach (var mapping in await connectedTables.ListFieldsAsync(table.Id, ct))
        {
            if (!destinationById.TryGetValue(mapping.AppFieldId, out var destinationField) || !destinationField.Fid.HasValue)
            {
                await connectedTables.RemoveFieldAsync(mapping.Id, ct);
                Issue(ConnectedIssueOutcomes.Warning, "destination_field_deleted", $"The field for \"{mapping.SourceFieldName}\" was deleted from this table, so it is no longer connected.", sourceFid: mapping.SourceFid);
                continue;
            }
            var sourceField = source.FindVisible(mapping.SourceFid);
            string? problem = null, state = null;
            ConnectedFieldPlan? plan = null;
            if (sourceField is null)
                (problem, state) = ($"\"{mapping.SourceFieldName}\" is no longer available in the source (deleted, or hidden from the connected user). Its values were kept.", ConnectedFieldStates.SourceMissing);
            else if (sourceField.IsEncrypted && !access.AllowsEncryptedFields)
                (problem, state) = ($"\"{mapping.SourceFieldName}\" is encrypted, and the source realm's approval does not allow copying encrypted fields.", ConnectedFieldStates.SourceMissing);
            else if (!ConnectedTypeMapper.IsCompatible(plan = ConnectedTypeMapper.Plan(sourceField, access.IsCrossTenant), destinationField.TypeCode))
                (problem, state) = ($"\"{mapping.SourceFieldName}\" changed type in the source and can no longer fill \"{destinationField.Label ?? destinationField.Name}\". Its values were kept.", ConnectedFieldStates.TypeConflict);

            if (problem is not null)
            {
                partial = true;
                if (mapping.State != state) await connectedTables.SetFieldStateAsync(mapping.Id, state!, ct);
                Issue(ConnectedIssueOutcomes.Warning, state!, problem, sourceFid: mapping.SourceFid);
                continue;
            }
            if (mapping.State != ConnectedFieldStates.Connected) await connectedTables.SetFieldStateAsync(mapping.Id, ConnectedFieldStates.Connected, ct);
            active.Add(new ActiveField(mapping, sourceField!, destinationField, plan!));
        }
        if (active.Count == 0)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "None of the connected fields can be read from the source any more.");
        if (active.Count > _options.MaxConnectedFields)
            throw new ConnectedTableException(ConnectedErrorCodes.LimitExceeded, $"A connected table can have at most {_options.MaxConnectedFields} connected fields.");

        // ── Key, filter, fields to read ─────────────────────────────────────────────────────────────────────
        var recordIdField = source.AllFields.FirstOrDefault(f => f.IsSystem && f.Fid == ConnectedSystemFids.RecordId)
            ?? throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The source table has no Record ID# field.");
        var dateModifiedField = source.AllFields.FirstOrDefault(f => f.IsSystem && f.Fid == ConnectedSystemFids.DateModified);
        var keyField = snapshot.RefreshKeySourceFid == ConnectedSystemFids.RecordId
            ? recordIdField
            : source.FindVisible(snapshot.RefreshKeySourceFid)
              ?? throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The refresh key field is no longer available in the source. Choose another refresh key.");

        var userFilter = snapshot.FilterEnabled ? ParseFilter(snapshot.FilterJson) : null;
        if (userFilter is not null)
        {
            var visibleFids = source.VisibleFields.Select(f => (long)f.Fid!.Value).ToHashSet();
            if (FilterFids(userFilter).Any(fid => !visibleFids.Contains(fid)))
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The table's filter uses a source field that is no longer available. Edit the filter.");
        }
        var baseFilter = And(userFilter, source.Access.ViewFilter);
        var restrict = source.Access.RestrictToCreatedBy;

        var computedActive = active.Any(a => PhysicalNaming.IsComputedTypeCode(a.Source.TypeCode));
        var readFields = BuildReadFields(source, active, keyField, recordIdField, dateModifiedField, baseFilter, computedActive);
        var projectionFields = computedActive ? source.VisibleFields : null;
        var keyReadFields = Distinct([recordIdField, keyField], FilterLookupFields(source, baseFilter));

        // ── Incremental or full ─────────────────────────────────────────────────────────────────────────────
        // Only scheduled refreshes take the incremental shortcut; a manual "Refresh data" always compares everything.
        var dateCreatedField = source.AllFields.FirstOrDefault(f => f.IsSystem && f.Fid == ConnectedSystemFids.DateCreated);
        var incremental = _options.IncrementalRefresh && run.TriggeredBy == ConnectedRunTriggers.Schedule
            && !computedActive && dateModifiedField is not null && dateCreatedField is not null && !run.ConfirmMassDelete
            && table.LastSourceWatermark is not null && table.WatermarkVersion == snapshot.FullRefreshVersion
            && table.IncrementalRunCount < Math.Max(1, _options.FullRefreshEvery);
        var runStartedUtc = DateTime.UtcNow;
        FilterGroup? readFilter = baseFilter;
        if (incremental)
        {
            // A record never edited has no Date Modified, so "changed since" means modified OR created since.
            var since = table.LastSourceWatermark!.Value.AddMinutes(-Math.Max(0, _options.WatermarkOverlapMinutes))
                .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            readFilter = And(baseFilter, new FilterGroup
            {
                Logic = "or",
                Nodes =
                [
                    new FilterNode { Condition = new FilterCondition { FieldId = ConnectedSystemFids.DateModified, Operator = "gte", Value = since } },
                    new FilterNode { Condition = new FilterCondition { FieldId = ConnectedSystemFids.DateCreated, Operator = "gte", Value = since } },
                ]
            });
            readFields = Distinct(readFields, [dateCreatedField!]);
        }

        // ── Size limits, then start ─────────────────────────────────────────────────────────────────────────
        var maxId = await access.Scope.GetMaxRecordIdAsync(source.Table, recordIdField, ct);
        var total = await access.Scope.CountAsync(source.Table, keyReadFields, baseFilter, restrict, ct);
        if (total > _options.MaxSourceRows)
            throw new ConnectedTableException(ConnectedErrorCodes.LimitExceeded, $"The source has {total:N0} records to connect; a connected table can hold at most {_options.MaxSourceRows:N0}. Add a filter.");

        if (!await runs.MarkRunningAsync(run.Id, maxId, incremental, ct)) return ConnectedRunOutcome.Done; // cancelled while waiting
        run.Status = ConnectedRunStatuses.Running;
        run.IsIncremental = incremental;
        await audit.LogActivityAsync("ConnectedRefreshStarted", "ConnectedTable", table.PublicId.ToString(), destination.Name, table.AppId, ct: ct);

        if (keyField.Fid != ConnectedSystemFids.RecordId)
            await CheckKeyIsUniqueAsync(access, source, keyField, keyReadFields, baseFilter, restrict, maxId, ct);

        // ── Main pass ───────────────────────────────────────────────────────────────────────────────────────
        var chunkSize = _options.EffectiveChunkSize;
        var emails = new Dictionary<long, string?>();
        var needsEmails = active.Any(a => a.Plan.Conversion == ConnectedConversions.UserToEmail);
        var destinationActive = active.Select(a => a.Destination).ToList();
        long cursor = 0;
        var cancelled = false;
        while (true)
        {
            var rows = await access.Scope.ReadChunkAsync(source.Table, readFields, readFilter, cursor, maxId, chunkSize, restrict, ct);
            if (rows.Count == 0) break;
            cursor = RecordId(rows[^1]);
            var computed = projectionFields is null ? null : await access.Scope.ProjectAsync(source.Table, projectionFields, rows, ct);

            if (needsEmails) await LoadEmailsAsync(rows, computed, active, emails, ct);
            string? EmailOf(long id) => emails.GetValueOrDefault(id);

            var items = new List<Item>(rows.Count);
            var chunkKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var computedRow = computed?[i];
                var key = ConnectedValues.Key(ConnectedValues.Read(keyField, row, computedRow));
                if (key is null)
                {
                    run.Skipped++;
                    Issue(ConnectedIssueOutcomes.Skipped, "blank_refresh_key", "The record has no value in the refresh key field.", RecordId(row), keyField.Fid);
                    continue;
                }
                if (!chunkKeys.Add(key))
                {
                    run.Skipped++;
                    Issue(ConnectedIssueOutcomes.Skipped, "duplicate_refresh_key", "Another record has the same refresh key value.", RecordId(row), keyField.Fid);
                    continue;
                }
                var values = new Dictionary<long, object?>(active.Count);
                var canonical = new Dictionary<long, string?>(active.Count);
                var hashInput = new List<(long, string?)>(active.Count);
                foreach (var a in active)
                {
                    var fid = (long)a.Destination.Fid!.Value;
                    var value = ConnectedValues.Convert(a.Plan.Conversion, ConnectedValues.Read(a.Source, row, computedRow), EmailOf);
                    values[fid] = value;
                    var c = ConnectedValues.Normalize(a.Destination.TypeCode, value);
                    canonical[fid] = c;
                    hashInput.Add((fid, c));
                }
                items.Add(new Item(key, values, canonical, ConnectedValues.Hash(hashInput, snapshot.MappingVersion)));
            }
            run.RowsRead += rows.Count;

            await WriteItemsAsync(run, table, destination, destinationFields, destinationActive, items, Issue, ct);

            if (issues.Count >= 200) { await runs.AddIssuesAsync(issues, ct); issues.Clear(); }
            await runs.SaveProgressAsync(run, ct);
            if (await runs.IsCancelRequestedAsync(run.Id, ct)) { cancelled = true; break; }
            if (rows.Count < chunkSize) break;
            if (_options.ChunkDelayMilliseconds > 0) await Task.Delay(_options.ChunkDelayMilliseconds, ct);
        }

        if (cancelled)
        {
            await runs.AddIssuesAsync(issues, ct);
            await runs.CompleteAsync(run, ConnectedRunStatuses.Cancelled, "Cancelled. Records already refreshed stay; nothing was removed.", ct);
            return ConnectedRunOutcome.Done;
        }

        // ── Mirror phase ────────────────────────────────────────────────────────────────────────────────────
        if (snapshot.UpdateMode == ConnectedUpdateModes.Mirror)
        {
            var seenInSource = incremental
                ? await StampAllKeysAsync(access, source, keyField, keyReadFields, baseFilter, restrict, maxId, table.Id, run.Id, ct)
                : run.RowsRead - run.Skipped;
            var stale = await writer.CountStaleAsync(table.Id, run.Id, ct);
            var unmanaged = await writer.CountUnmanagedAsync(destination, table.Id, ct);
            var toDelete = stale + unmanaged;
            if (toDelete > 0)
            {
                var live = await writer.CountLiveRecordsAsync(destination, ct);
                var emptySource = seenInSource == 0 && live > 0;
                if (!run.ConfirmMassDelete && (emptySource || toDelete * 100 > (long)snapshot.MassDeleteGuardPct * Math.Max(1, live)))
                {
                    run.HeldDeleteCount = toDelete;
                    var reason = emptySource
                        ? $"The source returned no records, so this refresh would remove all {live:N0} records. Nothing was removed: confirm, or check the connection's permissions and the filter."
                        : $"This refresh would remove {toDelete:N0} of {live:N0} records. Nothing was removed: confirm the deletion, or check the connection's permissions and the filter.";
                    await runs.AddIssuesAsync(issues, ct);
                    await runs.CompleteAsync(run, ConnectedRunStatuses.Held, reason, ct);
                    await connectedTables.SetStatusAsync(table.Id, ConnectedTableStatuses.Held, reason, ct);
                    await notifier.NotifyHeldAsync(table, destination.Name, toDelete, live, ct);
                    return ConnectedRunOutcome.Done;
                }
                run.Deleted += await DeleteAllAsync(destination, table.Id, () => writer.ListStaleAsync(destination, table.Id, run.Id, DeleteBatch, ct), ct);
                run.Deleted += await DeleteAllAsync(destination, table.Id, () => writer.ListUnmanagedAsync(destination, table.Id, DeleteBatch, ct), ct);
            }
        }

        // ── Finish ──────────────────────────────────────────────────────────────────────────────────────────
        await runs.AddIssuesAsync(issues, ct);
        var status = partial || run.Skipped > 0 || run.Errored > 0 ? ConnectedRunStatuses.Partial : ConnectedRunStatuses.Success;
        await runs.CompleteAsync(run, status, status == ConnectedRunStatuses.Partial ? "Some fields or records could not be refreshed. See the issues." : null, ct);
        await connectedTables.RecordSuccessAsync(table.Id, DateTime.UtcNow,
            computedActive ? null : runStartedUtc, computedActive ? null : snapshot.FullRefreshVersion, incremental, ct);

        var liveNow = await writer.CountLiveRecordsAsync(destination, ct);
        await control.RecordLinkRunAsync(consumerTenantId, table.PublicId, liveNow, ct);
        var consumerName = (await control.GetTenantAsync(consumerTenantId, ct))?.Name ?? "another realm";
        await access.Scope.LogProviderReadAsync(source.Table, $"{consumerName} › {destination.Name}", run.RowsRead, ct);
        await audit.LogActivityAsync("ConnectedRefreshCompleted", "ConnectedTable", table.PublicId.ToString(),
            $"{destination.Name}: {run.Inserted:N0} added, {run.Updated:N0} updated, {run.Reverted:N0} restored, {run.Deleted:N0} removed", table.AppId, ct: ct);
        return ConnectedRunOutcome.Done;
    }

    /// <summary>Classifies a chunk against the destination and writes it in one transaction. If the destination rejects the
    /// chunk (for example a Unique setting on a connected field), the records are written one by one so one bad record
    /// doesn't stop the others.</summary>
    private async Task WriteItemsAsync(ConnectedRun run, ConnectedTable table, AppTable destination, IReadOnlyList<AppField> destinationFields,
        IReadOnlyList<AppField> destinationActive, IReadOnlyList<Item> items, Action<string, string, string, long?, int?> issue, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var syncRows = await writer.GetSyncRowsAsync(table.Id, items.Select(i => i.Key).ToList(), ct);
        var locals = await writer.GetLocalRowsAsync(destination, destinationActive, syncRows.Values.Select(s => s.LocalRecordId).ToList(), ct);

        var inserts = new List<ConnectedInsert>();
        var updates = new List<ConnectedUpdate>();
        var touches = new List<ConnectedTouch>();
        long inserted = 0, updated = 0, reverted = 0, unchanged = 0;
        foreach (var item in items)
        {
            if (!syncRows.TryGetValue(item.Key, out var syncRow))
            {
                inserts.Add(new ConnectedInsert(item.Key, item.Hash, item.Values));
                inserted++;
                continue;
            }
            if (!locals.TryGetValue(syncRow.LocalRecordId, out var local))
            {
                // The local record was deleted outside the module: put it back.
                inserts.Add(new ConnectedInsert(item.Key, item.Hash, item.Values));
                reverted++;
                continue;
            }
            var changed = new Dictionary<long, object?>();
            foreach (var field in destinationActive)
            {
                var fid = (long)field.Fid!.Value;
                if (!string.Equals(ConnectedValues.Normalize(field.TypeCode, ConnectedValues.Read(field, local, null)), item.Canonical[fid], StringComparison.Ordinal))
                    changed[fid] = item.Values[fid];
            }
            if (changed.Count == 0)
            {
                touches.Add(new ConnectedTouch(item.Key, item.Hash));
                unchanged++;
            }
            else
            {
                updates.Add(new ConnectedUpdate(item.Key, syncRow.LocalPublicId, item.Hash, changed));
                // Same source hash but different local values: someone edited the copy; the refresh puts it back.
                if (syncRow.SourceHash.AsSpan().SequenceEqual(item.Hash)) reverted++;
                else updated++;
            }
        }

        try
        {
            await writer.WriteChunkAsync(destination, destinationFields, table.Id, run.Id, inserts, updates, touches, ct);
            run.Inserted += inserted;
            run.Updated += updated;
            run.Reverted += reverted;
            run.Unchanged += unchanged;
        }
        catch (Exception ex) when (IsRowRejection(ex))
        {
            run.Unchanged += unchanged;
            await writer.WriteChunkAsync(destination, destinationFields, table.Id, run.Id, [], [], touches, ct);
            foreach (var insert in inserts)
            {
                if (await TryWriteAsync(() => writer.WriteChunkAsync(destination, destinationFields, table.Id, run.Id, [insert], [], [], ct))) run.Inserted++;
                else { run.Errored++; issue(ConnectedIssueOutcomes.Errored, "write_rejected", "The destination table rejected this record (for example a Unique setting on a connected field).", null, null); }
            }
            foreach (var update in updates)
            {
                if (await TryWriteAsync(() => writer.WriteChunkAsync(destination, destinationFields, table.Id, run.Id, [], [update], [], ct))) run.Updated++;
                else { run.Errored++; issue(ConnectedIssueOutcomes.Errored, "write_rejected", "The destination table rejected the new values of this record (for example a Unique setting on a connected field).", null, null); }
            }
        }
    }

    private static async Task<bool> TryWriteAsync(Func<Task> write)
    {
        try { await write(); return true; }
        catch (Exception ex) when (IsRowRejection(ex)) { return false; }
    }

    private async Task<long> DeleteAllAsync(AppTable destination, long connectedTableId, Func<Task<IReadOnlyList<Guid>>> next, CancellationToken ct)
    {
        long deleted = 0;
        while (true)
        {
            var batch = await next();
            if (batch.Count == 0) return deleted;
            await writer.DeleteAsync(destination, connectedTableId, batch, ct);
            deleted += batch.Count;
            if (batch.Count < DeleteBatch) return deleted;
        }
    }

    /// <summary>A custom refresh key must be unique and never blank over the records being connected; checked before anything is written.</summary>
    private async Task CheckKeyIsUniqueAsync(ConnectedSourceAccess access, ConnectedSourceTable source, AppField keyField, IReadOnlyList<AppField> readFields,
        FilterGroup? filter, long? restrict, long maxId, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long cursor = 0;
        while (true)
        {
            var rows = await access.Scope.ReadChunkAsync(source.Table, readFields, filter, cursor, maxId, _options.EffectiveChunkSize, restrict, ct);
            if (rows.Count == 0) return;
            cursor = RecordId(rows[^1]);
            foreach (var row in rows)
            {
                var key = ConnectedValues.Key(ConnectedValues.Read(keyField, row, null));
                if (key is null)
                    throw new ConnectedTableException(ConnectedErrorCodes.RefreshKeyNotUnique, $"The refresh key \"{keyField.Label ?? keyField.Name}\" is blank on some source records. Choose a refresh key that always has a value.");
                if (!keys.Add(key))
                    throw new ConnectedTableException(ConnectedErrorCodes.RefreshKeyNotUnique, $"The refresh key \"{keyField.Label ?? keyField.Name}\" is no longer unique in the source. Choose another refresh key.");
            }
            if (rows.Count < _options.EffectiveChunkSize) return;
        }
    }

    /// <summary>Incremental mirror refresh: reads only the keys of every connected source record and marks them as seen, so
    /// records that still exist (changed or not) are not taken for deleted. Returns how many records the source has.</summary>
    private async Task<long> StampAllKeysAsync(ConnectedSourceAccess access, ConnectedSourceTable source, AppField keyField, IReadOnlyList<AppField> readFields,
        FilterGroup? filter, long? restrict, long maxId, long connectedTableId, long runId, CancellationToken ct)
    {
        long seen = 0, cursor = 0;
        while (true)
        {
            var rows = await access.Scope.ReadChunkAsync(source.Table, readFields, filter, cursor, maxId, _options.EffectiveChunkSize, restrict, ct);
            if (rows.Count == 0) return seen;
            cursor = RecordId(rows[^1]);
            var keys = rows.Select(r => ConnectedValues.Key(ConnectedValues.Read(keyField, r, null))).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            await writer.StampSeenAsync(connectedTableId, runId, keys, ct);
            seen += keys.Count;
            if (rows.Count < _options.EffectiveChunkSize) return seen;
        }
    }

    private async Task LoadEmailsAsync(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<IReadOnlyDictionary<long, object?>>? computed,
        IReadOnlyList<ActiveField> active, Dictionary<long, string?> emails, CancellationToken ct)
    {
        var missing = new HashSet<long>();
        for (var i = 0; i < rows.Count; i++)
            foreach (var a in active.Where(a => a.Plan.Conversion == ConnectedConversions.UserToEmail))
                foreach (var id in ConnectedValues.UserIds(ConnectedValues.Read(a.Source, rows[i], computed?[i])))
                    if (!emails.ContainsKey(id)) missing.Add(id);
        if (missing.Count == 0) return;
        var found = await users.GetIdentitiesByIdsAsync(missing, ct);
        foreach (var id in missing) emails[id] = found.TryGetValue(id, out var u) ? u.Email : null;
    }

    private async Task FailAsync(ConnectedRun run, ConnectedTable table, AppTable? destination, string message, bool gateFailure, CancellationToken ct)
    {
        await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, message, ct);
        var failures = await connectedTables.RecordFailureAsync(table.Id, ct);
        if (gateFailure) await connectedTables.SetStatusAsync(table.Id, ConnectedTableStatuses.NeedsAttention, message, ct);

        var scheduleDisabled = false;
        if (run.TriggeredBy == ConnectedRunTriggers.Schedule && failures >= _options.FailureThreshold
            && ConnectedScheduling.Parse(table.ScheduleJson) is { IsAutomatic: true } schedule)
        {
            schedule.Enabled = false;
            await connectedTables.DisableScheduleAsync(table.Id, ConnectedScheduling.Serialize(schedule),
                $"The schedule was switched to Manual only after {failures} failed refreshes in a row. Last error: {message}", ct);
            scheduleDisabled = true;
        }

        var tableName = destination?.Name ?? table.SourceTableName;
        await audit.LogActivityAsync("ConnectedRefreshFailed", "ConnectedTable", table.PublicId.ToString(), $"{tableName}: {message}", table.AppId, ct: ct);
        // A scheduled table emails on every failure; a manual refresh shows its error on screen already.
        if (run.TriggeredBy != ConnectedRunTriggers.Manual || scheduleDisabled)
            await notifier.NotifyFailureAsync(table, tableName, message, scheduleDisabled, ct);
    }

    private static IReadOnlyList<AppField> BuildReadFields(ConnectedSourceTable source, IReadOnlyList<ActiveField> active, AppField keyField,
        AppField recordIdField, AppField? dateModifiedField, FilterGroup? filter, bool computedActive)
    {
        // Projection needs every field a formula / lookup / summary may refer to: read everything the user can see.
        if (computedActive) return Distinct(source.VisibleFields.Concat([recordIdField, keyField]), FilterLookupFields(source, filter), dateModifiedField);
        return Distinct(active.Select(a => a.Source).Concat([recordIdField, keyField]), FilterLookupFields(source, filter), dateModifiedField);
    }

    /// <summary>Fields a filter refers to (so the record repository knows their types when building the WHERE clause).</summary>
    private static IEnumerable<AppField> FilterLookupFields(ConnectedSourceTable source, FilterGroup? filter)
    {
        if (filter is null) return [];
        var fids = FilterFids(filter).ToHashSet();
        return source.AllFields.Where(f => f.Fid.HasValue && fids.Contains(f.Fid.Value));
    }

    private static IReadOnlyList<AppField> Distinct(IEnumerable<AppField> fields, IEnumerable<AppField> more, AppField? extra = null) =>
        fields.Concat(more).Concat(extra is null ? [] : [extra]).GroupBy(f => f.Id).Select(g => g.First()).ToList();

    private static IEnumerable<long> FilterFids(FilterGroup group)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                yield return c.FieldId;
                if (c.ValueFieldId is { } other) yield return other;
            }
            if (node.Group is { } g) foreach (var fid in FilterFids(g)) yield return fid;
        }
    }

    public static FilterGroup? ParseFilter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var group = JsonSerializer.Deserialize<FilterGroup>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return group is { Nodes.Count: > 0 } ? group : null;
        }
        catch (JsonException) { return null; }
    }

    private static FilterGroup? And(FilterGroup? a, FilterGroup? b)
    {
        if (a is null || a.Nodes.Count == 0) return b is { Nodes.Count: > 0 } ? b : null;
        if (b is null || b.Nodes.Count == 0) return a;
        return new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = a }, new FilterNode { Group = b }] };
    }

    private static long RecordId(IReadOnlyDictionary<string, object?> row) => Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture);

    public static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        DbException { IsTransient: true } => true,
        _ when ex.InnerException is { } inner => IsTransient(inner),
        _ => false,
    };

    private static bool IsRowRejection(Exception ex) =>
        ex is ConflictException or DuplicateException or RecordConstraintViolationException or BadRequestException
        || ex is DbException { IsTransient: false };
}
