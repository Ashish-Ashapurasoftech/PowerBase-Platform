using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Worker;

/// <summary>The worker's commands. Each one is safe to run again: map/plan refuse to redo a planned table, run skips done
/// chunks, and every chunk write replaces its own range.</summary>
public sealed class Commands(Composition c)
{
    private readonly ILogger _log = c.Logs.CreateLogger("qbm");
    // Matches the queue's maxDeliveryCount (infra/qb-migration/main.bicep).
    private const int MaxDeliveries = 5;

    /// <summary>Creates the qbm schema in this tenant only.</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sql"), "*.sql").Order(StringComparer.Ordinal))
            await c.State().InitAsync(await File.ReadAllTextAsync(path, ct), ct);
        _log.LogInformation("qbm schema ready in tenant {Tenant}", c.TenantId);
    }

    /// <summary>Matches QuickBase tables and fields to the PowerBase app the QBL import created, and records the map.</summary>
    public async Task<long> MapAsync(string qbAppId, long pbAppId, long runAsUserId, CancellationToken ct)
    {
        var qb = c.QuickBase();
        var pb = c.PowerBase();
        var state = c.State();
        var pbTables = await pb.TablesAsync(pbAppId, ct);
        var qbTables = await qb.GetTablesAsync(qbAppId, ct);
        var references = await pb.ReferencesAsync(pbAppId, ct);
        var runId = await state.CreateRunAsync(c.Config["QuickBase:RealmHostname"]!, qbAppId, pbAppId, runAsUserId, ct);

        var matched = new List<(QbTable Qb, QbmPowerBaseReader.PbTable Pb, IReadOnlyList<QbFieldMapping> Fields)>();
        foreach (var qt in qbTables)
        {
            var pt = MatchTable(qt, pbTables);
            if (pt is null)
            {
                await state.AddIssuesAsync(runId, null, null, [new("error", "QBM_TABLE_NOT_FOUND", $"QuickBase table '{qt.Name}' ({qt.Id}) has no PowerBase table.")], ct);
                continue;
            }
            var mappings = await ReleaseEmptyMissingAsync(qt.Id,
                QbFieldMapper.Map(await qb.GetFieldsAsync(qt.Id, ct), await pb.FieldsAsync(pt.Id, ct)), ct);
            matched.Add((qt, pt, mappings));
        }

        // Order only matters for references translated through a parent key; tables in a cycle of those are blocked.
        var byKey = references.Where(r => r.ParentKey.Fid != QbSystemFids.RecordId).ToList();
        var (tiers, cyclic) = QbTierPlanner.Plan(matched.Select(m => m.Pb.Id), byKey.Select(r => (r.ChildTableId, r.ParentTableId)));
        foreach (var (qt, pt, mappings) in matched)
        {
            var fields = mappings.ToList();
            var problems = new List<QbIssue>();
            if (cyclic.Contains(pt.Id))
                problems.Add(new("error", "QBM_KEY_REFERENCE_CYCLE", $"'{pt.Name}' is in a cycle of references matched by parent key; load order can't be decided."));
            foreach (var r in byKey.Where(r => r.ChildTableId == pt.Id))
            {
                if (r.ParentTableId == pt.Id)
                    problems.Add(new("error", "QBM_SELF_KEY_REFERENCE", $"Field {r.ReferenceFid} references its own table by key; not supported in the POC."));
                if (r.ParentKey.IsEncrypted)
                    problems.Add(new("error", "QBM_ENCRYPTED_PARENT_KEY", $"Field {r.ReferenceFid}: the parent key field is encrypted, so it can't be matched."));
            }
            var tableMapId = await state.SaveTableMapAsync(runId, qt, pt.Id, tiers.GetValueOrDefault(pt.Id), fields, ct);
            var issues = fields.Where(f => f.Reason is not null).Select(f => new QbIssue(
                f.Action == QbFieldAction.Block ? "error" : "info", "QBM_FIELD_" + QbmStateStore.ActionName(f.Action).ToUpperInvariant().Replace('-', '_'),
                $"{f.Qb.Label} ({f.Qb.Id}): {f.Reason}", null, f.Qb.Id)).Concat(problems).ToList();
            await state.AddIssuesAsync(runId, tableMapId, null, issues, ct);
            if (problems.Count > 0) await state.SetTableStatusAsync(tableMapId, "blocked", ct);

            var counts = fields.GroupBy(f => f.Action).ToDictionary(g => g.Key, g => g.Count());
            _log.LogInformation("{Qb} -> {Pb} (tier {Tier}): {Counts}{Blocked}", qt.Name, pt.Name, tiers.GetValueOrDefault(pt.Id),
                string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")),
                QbFieldMapper.IsLoadable(fields) && problems.Count == 0 ? "" : "  BLOCKED");
        }
        _log.LogInformation("Run {Run} mapped; see qbm.FieldMap / qbm.Issue", runId);
        return runId;
    }

    /// <summary>A QuickBase field the QBL import didn't create blocks its table, unless QuickBase confirms the field is
    /// empty in every record: then there is nothing to lose and it is skipped (with the reason recorded). Checked again
    /// on every run, so a value added in QuickBase since blocks the table again.</summary>
    private async Task<IReadOnlyList<QbFieldMapping>> ReleaseEmptyMissingAsync(string qbTableId, IReadOnlyList<QbFieldMapping> mappings, CancellationToken ct)
    {
        var result = mappings.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            var m = result[i];
            if (m.Action != QbFieldAction.Block || m.PbFid is not null || m.Reason?.StartsWith("No PowerBase field", StringComparison.Ordinal) != true)
                continue;
            var withValue = await c.QuickBase().QueryAsync(qbTableId, [QbSystemFids.RecordId], $"{{{m.Qb.Id}.XEX.''}}", 0, 1, ct);
            if (withValue.TotalRecords == 0)
                result[i] = m with { Action = QbFieldAction.Skip, Reason = "No PowerBase field (the QBL import didn't create it), and it is empty in every QuickBase record." };
        }
        return result;
    }

    private static QbmPowerBaseReader.PbTable? MatchTable(QbTable qt, IReadOnlyList<QbmPowerBaseReader.PbTable> pbTables) =>
        (qt.Alias is { Length: > 0 } alias ? pbTables.FirstOrDefault(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase)) : null)
        ?? pbTables.SingleOrDefault(p => string.Equals(p.Name.Trim(), qt.Name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads QuickBase counts and Record ID ranges and writes the chunk ledger. A destination table must be empty.</summary>
    public async Task PlanAsync(long runId, bool extend, CancellationToken ct)
    {
        var qb = c.QuickBase();
        var state = c.State();
        var rowsPerChunk = c.Config.GetValue("Migration:RowsPerChunk", 12000);
        if (extend) { await ExtendAsync(runId, rowsPerChunk, ct); return; }
        var maps = await state.TableMapsAsync(runId, ct);
        var settled = maps.Where(t => t.Status != "mapped").ToList();
        if (settled.Count > 0)
            _log.LogInformation("{Count} table(s) not planned again: {Statuses}", settled.Count,
                string.Join(", ", settled.GroupBy(t => t.Status).Select(g => $"{g.Count()} {g.Key}")));
        foreach (var t in settled.Where(t => t.Status == "blocked"))
        {
            if (await state.OtherRunForTableAsync(runId, t.PbTableId, ct) is { } owner)
                _log.LogInformation("{Table}: blocked in this run, loaded by run {Owner}", t.QbTableName, owner);
            else
                _log.LogWarning("{Table}: blocked; see qbm.Issue for this table", t.QbTableName);
        }
        foreach (var t in maps)
        {
            if (t.Status != "mapped") continue;
            if (await state.OtherRunForTableAsync(runId, t.PbTableId, ct) is { } owner)
            {
                await state.AddIssuesAsync(runId, t.Id, null, [new("info", "QBM_TABLE_IN_OTHER_RUN", $"Already in run {owner}; use plan --run {owner} --extend for records created since.")], ct);
                await state.SetTableStatusAsync(t.Id, "skipped", ct);
                _log.LogInformation("{Table}: already in run {Owner}, skipped", t.QbTableName, owner);
                continue;
            }
            var existing = await state.CountRowsAsync(t.PbTableId, ct);
            if (existing > 0)
            {
                await state.AddIssuesAsync(runId, t.Id, null, [new("error", "QBM_TABLE_NOT_EMPTY", $"PowerBase table {t.PbTableId} already has {existing} rows; the migration only loads empty tables.")], ct);
                await state.SetTableStatusAsync(t.Id, "blocked", ct);
                continue;
            }
            var first = await qb.QueryAsync(t.QbTableId, [QbSystemFids.RecordId], null, 0, 1, ct);
            if (first.TotalRecords == 0)
            {
                await state.PlanChunksAsync(runId, t.Id, 0, 0, 0, [], ct);
                _log.LogInformation("{Table}: empty in QuickBase", t.QbTableName);
                continue;
            }
            var last = await qb.QueryAsync(t.QbTableId, [QbSystemFids.RecordId], null, first.TotalRecords - 1, 1, ct);
            var min = first.Rows[0].RecordId;
            var max = last.Rows[0].RecordId;
            var ranges = QbChunkPlanner.Plan(min, max, first.TotalRecords, rowsPerChunk);
            await state.PlanChunksAsync(runId, t.Id, first.TotalRecords, min, max, ranges, ct);
            _log.LogInformation("{Table}: {Count} records, ids {Min}..{Max}, {Chunks} chunks", t.QbTableName, first.TotalRecords, min, max, ranges.Count);
        }
    }

    /// <summary>For tables already planned or loaded: refreshes QuickBase's count and Record ID range and plans chunks only for
    /// records created above the planned ranges. Edits and deletes inside loaded ranges are picked up by <c>run --reload</c>.</summary>
    private async Task ExtendAsync(long runId, int rowsPerChunk, CancellationToken ct)
    {
        var qb = c.QuickBase();
        var state = c.State();
        foreach (var t in await state.TableMapsAsync(runId, ct))
        {
            if (t.Status is not ("planned" or "loading" or "loaded" or "validated")) { _log.LogInformation("{Table}: {Status}, not extended", t.QbTableName, t.Status); continue; }
            var first = await qb.QueryAsync(t.QbTableId, [QbSystemFids.RecordId], null, 0, 1, ct);
            if (first.TotalRecords == 0)
            {
                await state.ExtendChunksAsync(runId, t.Id, 0, 0, 0, [], ct);
                _log.LogInformation("{Table}: empty in QuickBase", t.QbTableName);
                continue;
            }
            var last = await qb.QueryAsync(t.QbTableId, [QbSystemFids.RecordId], null, first.TotalRecords - 1, 1, ct);
            var min = first.Rows[0].RecordId;
            var max = last.Rows[0].RecordId;
            var from = Math.Max(await state.PlannedEndAsync(t.Id, ct), min);
            var ranges = max >= from ? QbChunkPlanner.Plan(from, max, first.TotalRecords, rowsPerChunk) : [];
            await state.ExtendChunksAsync(runId, t.Id, first.TotalRecords, min, max, ranges, ct);
            _log.LogInformation("{Table}: {Count} records in QuickBase, ids {Min}..{Max}; {Chunks} new chunks from id {From}",
                t.QbTableName, first.TotalRecords, min, max, ranges.Count, from);
        }
    }

    /// <summary>Loads every chunk that isn't done, with N workers. "local" uses an in-process queue (POC without Azure);
    /// "servicebus" sends the chunks through the dedicated Service Bus queue and consumes them.</summary>
    public async Task<bool> RunAsync(long runId, string queue, bool reload, CancellationToken ct)
    {
        await using var runLock = await LockAsync(runId, ct);
        await ClearStopAsync(runId, ct);
        return await LoadAsync(runId, queue, reload, Control(runId), ct);
    }

    /// <summary>The load itself. Returns true when no chunk is left. Honours pause/stop between chunks.</summary>
    private async Task<bool> LoadAsync(long runId, string queue, bool reload, QbRunControl control, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        if (reload) _log.LogInformation("Run {Run}: {Count} done chunks set back to pending for reload", runId, await state.ReloadChunksAsync(runId, ct));
        var contexts = await ContextsAsync(run, ct);
        var pending = (await state.PendingChunksAsync(runId, ct)).Where(ch => contexts.ContainsKey(ch.TableMapId)).ToList();
        var workers = c.Config.GetValue("Migration:Workers", 2);
        _log.LogInformation("Run {Run}: {Count} chunks to load with {Workers} workers via {Queue}", runId, pending.Count, workers, queue);
        await state.SetRunStatusAsync(runId, "loading", ct);

        var processor = new QbmChunkProcessor(c.QuickBase(), c.Loader(), state, c.PowerBase(), c.Logs.CreateLogger<QbmChunkProcessor>());
        if (queue == "servicebus")
        {
            var connection = c.Config["ServiceBus:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ServiceBus:ConnectionString is required for --queue servicebus.");
            await using var client = new ServiceBusClient(connection);
            // Messages left by a stopped or crashed load of this run would load their chunk a second time next to the new ones.
            await SweepAsync(client, runId, ct);
            // Tier by tier, like the local queue: a child matched to parents by key starts only after its parents are in.
            foreach (var tier in pending.GroupBy(p => TierOf(contexts[p.TableMapId])).OrderBy(g => g.Key))
            {
                if (control.Stopped) break;
                await RunServiceBusAsync(client, runId, tier.ToList(), contexts, processor, workers, control, ct);
            }
        }
        else await RunLocalAsync(pending, contexts, processor, workers, control, ct);

        var left = await state.PendingChunksAsync(runId, ct);
        if (control.Stopped)
            _log.LogWarning("Run {Run}: stopped by an operator; {Left} chunks not done. Start again to continue.", runId, left.Count);
        else
            _log.LogInformation("Run {Run}: {Left} chunks not done", runId, left.Count);
        return left.Count == 0;
    }

    private QbRunControl Control(long runId) => new(
        token => c.State().ControlAsync(runId, token),
        TimeSpan.FromSeconds(c.Config.GetValue("Migration:ControlPollSeconds", 10)),
        command => _log.LogWarning("Run {Run}: operator asked to {Command}", runId, command.ToString().ToLowerInvariant()));

    /// <summary>One worker per run: a second start of the same run (Start clicked twice) stops here.</summary>
    private async Task<IAsyncDisposable> LockAsync(long runId, CancellationToken ct) =>
        await c.State().TryLockRunAsync(runId, ct)
        ?? throw new InvalidOperationException($"Run {runId} is already being worked on by another worker. Check 'status --run {runId}'.");

    /// <summary>Starting a run again is the operator's answer to an earlier stop. A pause stays: resume lifts it.</summary>
    private async Task ClearStopAsync(long runId, CancellationToken ct)
    {
        var state = c.State();
        switch (QbRunControl.Parse(await state.ControlAsync(runId, ct)))
        {
            case QbRunCommand.Stop:
                await state.SetControlAsync(runId, null, ct);
                _log.LogInformation("Run {Run}: the earlier stop is cleared; continuing where it ended", runId);
                break;
            case QbRunCommand.Pause:
                _log.LogWarning("Run {Run} is paused; nothing is loaded until 'resume --run {Run}'", runId, runId);
                break;
        }
    }

    private async Task<Dictionary<long, QbmTableContext>> ContextsAsync(QbmStateStore.RunRow run, CancellationToken ct, string[]? statuses = null)
    {
        var qb = c.QuickBase();
        var pb = c.PowerBase();
        var references = await pb.ReferencesAsync(run.PbAppId, ct);
        var options = new QbTransformOptions
        {
            FallbackUserId = run.RunAsUserId,
            PercentMultiplier = c.Config.GetValue("Migration:PercentMultiplier", 100m),
            DurationDivisor = c.Config.GetValue("Migration:DurationDivisor", 60000m),
        };
        var result = new Dictionary<long, QbmTableContext>();
        foreach (var t in await c.State().TableMapsAsync(run.Id, ct))
        {
            statuses ??= ["planned", "loading"];
            if (!statuses.Contains(t.Status)) continue;
            var pbFields = await pb.FieldsAsync(t.PbTableId, ct);
            // The mapping is rebuilt from the live fields, so a schema change since "map" can't slip through unnoticed.
            var mappings = await ReleaseEmptyMissingAsync(t.QbTableId, QbFieldMapper.Map(await qb.GetFieldsAsync(t.QbTableId, ct), pbFields), ct);
            if (!QbFieldMapper.IsLoadable(mappings))
            {
                _log.LogError("{Table}: fields are blocked now (schema changed since map?); skipped", t.QbTableName);
                continue;
            }
            var keyed = references.Where(r => r.ChildTableId == t.PbTableId && r.ParentKey.Fid != QbSystemFids.RecordId).ToList();
            // A link whose child field isn't loaded (in QuickBase it is a formula) has no value to match by key; PowerBase
            // computes it, so there is nothing to translate. Report it once instead of failing every chunk of the table.
            foreach (var r in keyed.Where(r => !mappings.Any(m => m.Action == QbFieldAction.Load && m.PbFid == r.ReferenceFid)))
                _log.LogWarning("{Table}: link field {Fid} is not loaded (formula in QuickBase); its key-matched values are left to PowerBase", t.QbTableName, r.ReferenceFid);
            var byKey = keyed.Where(r => mappings.Any(m => m.Action == QbFieldAction.Load && m.PbFid == r.ReferenceFid))
                .ToDictionary(r => r.ReferenceFid, r => r);
            var table = new AppTable { Id = t.PbTableId, AppId = run.PbAppId, Name = t.QbTableName };
            result[t.Id] = new QbmTableContext(c.TenantId, run.Id, t.Id, t.QbTableId, table, pbFields, mappings, byKey, options, t.Tier);
            if (t.Status == "planned") await c.State().SetTableStatusAsync(t.Id, "loading", ct);
        }
        return result;
    }

    private async Task RunLocalAsync(IReadOnlyList<QbmStateStore.ChunkRow> pending, Dictionary<long, QbmTableContext> contexts,
        QbmChunkProcessor processor, int workers, QbRunControl control, CancellationToken ct)
    {
        // Tier by tier: a child that matches parents by key must not start before its parents are in.
        foreach (var tier in pending.GroupBy(p => contexts[p.TableMapId]).GroupBy(g => TierOf(g.Key)).OrderBy(g => g.Key))
        {
            if (control.Stopped) return;
            var channel = Channel.CreateUnbounded<QbmStateStore.ChunkRow>();
            foreach (var chunk in tier.SelectMany(g => g)) channel.Writer.TryWrite(chunk);
            channel.Writer.Complete();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                {
                    // Pause waits here; stop leaves the rest of the chunks pending for the next start.
                    if (!await control.WaitUntilAllowedAsync(ct)) return;
                    try
                    {
                        var outcome = await processor.ProcessAsync(contexts[chunk.TableMapId], chunk.Id, new QbRange(chunk.FromRecordId, chunk.ToRecordId), ct);
                        if (outcome == QbmChunkProcessor.Outcome.Split) _log.LogWarning("Chunk {Chunk} split; run again to load the new half", chunk.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "Chunk {Chunk} failed (it stays not done; run again to retry)", chunk.Id);
                    }
                }
            }, ct)));
        }
    }

    private static int TierOf(QbmTableContext ctx) => ctx.Tier;

    private string QueueName => c.Config["ServiceBus:QueueName"] ?? "qb-migration-chunks";

    private static long RunIdOf(ServiceBusReceivedMessage message)
    {
        try { return JsonDocument.Parse(message.Body.ToString()).RootElement.GetProperty("runId").GetInt64(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return -1; }
    }

    private static long ChunkIdOf(ServiceBusReceivedMessage message)
    {
        try { return JsonDocument.Parse(message.Body.ToString()).RootElement.GetProperty("chunkId").GetInt64(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return -1; }
    }

    /// <summary>Removes this run's messages still in the queue (from a stopped or crashed load). Pending chunks are sent again
    /// right after, so nothing is lost; other runs' messages are put back.</summary>
    private async Task SweepAsync(ServiceBusClient client, long runId, CancellationToken ct)
    {
        await using var receiver = client.CreateReceiver(QueueName);
        var removed = 0;
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(3), ct);
            if (batch.Count == 0) break;
            var ours = 0;
            foreach (var m in batch)
            {
                if (RunIdOf(m) == runId) { await receiver.CompleteMessageAsync(m, ct); ours++; }
                else await receiver.AbandonMessageAsync(m, cancellationToken: ct);
            }
            removed += ours;
            if (ours == 0) break;
        }
        if (removed > 0) _log.LogInformation("Run {Run}: {Count} leftover queue messages from an earlier load removed", runId, removed);
    }

    private async Task RunServiceBusAsync(ServiceBusClient client, long runId, IReadOnlyList<QbmStateStore.ChunkRow> pending,
        Dictionary<long, QbmTableContext> contexts, QbmChunkProcessor processor, int workers, QbRunControl control, CancellationToken ct)
    {
        var queueName = QueueName;
        if (!await control.WaitUntilAllowedAsync(ct)) return;

        // Producer: one message per chunk. A duplicate message is harmless: a done chunk is skipped.
        await using (var sender = client.CreateSender(queueName))
            foreach (var batch in pending.Chunk(100))
                await sender.SendMessagesAsync(batch.Select(ch => new ServiceBusMessage(JsonSerializer.Serialize(new { runId, chunkId = ch.Id }))
                    { MessageId = $"qbm-{runId}-{ch.Id}-{ch.Attempts}", Subject = "qbm-chunk" }), ct);

        var byId = pending.ToDictionary(p => p.Id);
        var remaining = pending.Count;
        var finished = new TaskCompletionSource();
        await using var consumer = client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = workers,
            AutoCompleteMessages = false,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30),
        });
        consumer.ProcessMessageAsync += async args =>
        {
            var chunkId = ChunkIdOf(args.Message);
            if (RunIdOf(args.Message) != runId)
            {
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken); // another run's message
                return;
            }
            if (!byId.TryGetValue(chunkId, out var chunk))
            {
                // This run, but not a chunk of this tier's load (already done, or left from an earlier load): drop it.
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                return;
            }
            try
            {
                await processor.ProcessAsync(contexts[chunk.TableMapId], chunk.Id, new QbRange(chunk.FromRecordId, chunk.ToRecordId), args.CancellationToken);
                // ACK only now: the chunk's rows are committed (or it was already done).
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (QuickBaseRequestException ex)
            {
                // Retrying won't help (bad request, no access): dead-letter it for a person to look at.
                await args.DeadLetterMessageAsync(args.Message, "QuickBaseRequest", ex.Message, args.CancellationToken);
                _log.LogError("Chunk {Chunk} dead-lettered: {Error}", chunkId, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (args.Message.DeliveryCount < MaxDeliveries)
                {
                    _log.LogError(ex, "Chunk {Chunk} failed (attempt {Attempt} of {Max}); message abandoned for redelivery", chunkId, args.Message.DeliveryCount, MaxDeliveries);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                    return;
                }
                // Last attempt: dead-letter it ourselves and count it as finished, so the run ends instead of waiting for a
                // message the queue has already given up on. The chunk stays not done; fix the cause and run again.
                await args.DeadLetterMessageAsync(args.Message, "ChunkFailed", ex.Message, args.CancellationToken);
                _log.LogError(ex, "Chunk {Chunk} failed {Max} times; dead-lettered", chunkId, MaxDeliveries);
            }
            if (Interlocked.Decrement(ref remaining) == 0) finished.TrySetResult();
        };
        consumer.ProcessErrorAsync += args =>
        {
            _log.LogError(args.Exception, "Service Bus error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };
        await consumer.StartProcessingAsync(ct);

        // Operator controls: pause stops taking messages (the chunks in progress finish first), resume takes them again,
        // stop ends the load with the remaining messages left in the queue for the next start.
        var stopped = new TaskCompletionSource();
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poll = TimeSpan.FromSeconds(c.Config.GetValue("Migration:ControlPollSeconds", 10));
        var monitor = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var command = await control.CurrentAsync(monitorCts.Token);
                    if (command != QbRunCommand.Run && consumer.IsProcessing)
                    {
                        await consumer.StopProcessingAsync(CancellationToken.None);
                        _log.LogWarning("Run {Run}: {State}; chunks in progress finished, nothing new is taken", runId, command == QbRunCommand.Stop ? "stopping" : "paused");
                    }
                    if (command == QbRunCommand.Stop) { stopped.TrySetResult(); return; }
                    if (command == QbRunCommand.Run && !consumer.IsProcessing)
                    {
                        await consumer.StartProcessingAsync(monitorCts.Token);
                        _log.LogInformation("Run {Run}: resumed", runId);
                    }
                    await Task.Delay(poll, monitorCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        if (pending.Count > 0) await Task.WhenAny(finished.Task, stopped.Task).WaitAsync(ct);
        monitorCts.Cancel();
        await monitor;
        if (consumer.IsProcessing) await consumer.StopProcessingAsync(CancellationToken.None);
    }

    /// <summary>Creates (inactive, no email) or links the PowerBase users the loaded data named but couldn't find.</summary>
    public async Task UsersAsync(long runId, long appRoleId, long tenantRoleId, bool apply, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        var emails = await state.UnmappedEmailsAsync(runId, ct);
        var outcomes = await new QbmUserProvisioner(c.Tenant, c.Control)
            .ProvisionAsync(c.TenantId, run.PbAppId, appRoleId, tenantRoleId, run.RunAsUserId, emails, dryRun: !apply, ct);
        foreach (var o in outcomes) _log.LogInformation("{Email}: {Action} {User}", o.Email, o.Action, o.UserId);
        _log.LogInformation(apply ? "Done. Run the load again: chunks replace their rows, now with these users." : "Dry run. Add --apply to create them.");
    }

    /// <summary>After loading: per table, RecordCount and identity seed, then the count and id-range gates.</summary>
    public async Task<bool> FinalizeAsync(long runId, CancellationToken ct)
    {
        await using var runLock = await LockAsync(runId, ct);
        return await FinalizeCoreAsync(runId, ct);
    }

    private async Task<bool> FinalizeCoreAsync(long runId, CancellationToken ct)
    {
        var state = c.State();
        var allPassed = true;
        var left = await state.PendingChunksAsync(runId, ct);
        foreach (var t in await state.TableMapsAsync(runId, ct))
        {
            if (t.Status is not ("loading" or "loaded" or "validated")) continue;
            if (left.Any(ch => ch.TableMapId == t.Id))
            {
                _log.LogWarning("{Table}: {Count} chunks not done; not finalized", t.QbTableName, left.Count(ch => ch.TableMapId == t.Id));
                allPassed = false;
                continue;
            }
            var r = await state.FinalizeTableAsync(t.PbTableId, ct);
            var gate1 = r.Rows == (t.QbRecordCount ?? 0);
            var gate2 = (t.QbRecordCount ?? 0) == 0 || (r.MinId == t.QbMinRecordId && r.MaxId == t.QbMaxRecordId && r.IdentityAfter >= r.MaxId);
            var issues = new List<QbIssue>();
            if (!gate1) issues.Add(new("error", "QBM_GATE1_COUNT", $"QuickBase {t.QbRecordCount} records, PowerBase {r.Rows}."));
            if (!gate2) issues.Add(new("error", "QBM_GATE2_ID_RANGE", $"QuickBase ids {t.QbMinRecordId}..{t.QbMaxRecordId}, PowerBase {r.MinId}..{r.MaxId}, identity {r.IdentityAfter}."));
            await state.AddIssuesAsync(runId, t.Id, null, issues, ct);
            await state.SetTableStatusAsync(t.Id, gate1 && gate2 ? "loaded" : "loading", ct);
            allPassed &= gate1 && gate2;
            _log.LogInformation("{Table}: {Rows} rows (QB {Qb}), ids {Min}..{Max}, identity {Before} -> {After}; gate 1 {G1}, gate 2 {G2}",
                t.QbTableName, r.Rows, t.QbRecordCount, r.MinId, r.MaxId, r.IdentityBefore, r.IdentityAfter, gate1 ? "pass" : "FAIL", gate2 ? "pass" : "FAIL");
        }
        await state.SetRunStatusAsync(runId, allPassed ? "loaded" : "failed", ct);
        return allPassed;
    }

    /// <summary>Gates 3, 4 and 7 for every loaded table: every value compared with QuickBase (sample 1 = all chunks),
    /// references resolve, encrypted columns hold ciphertext. A table that passes is marked validated.</summary>
    public async Task<bool> ValidateAsync(long runId, double sample, CancellationToken ct)
    {
        await using var runLock = await LockAsync(runId, ct);
        return await ValidateCoreAsync(runId, sample, ct);
    }

    private async Task<bool> ValidateCoreAsync(long runId, double sample, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        var contexts = await ContextsAsync(run, ct, ["loaded", "validated"]);
        var validator = new QbmValidator(c.QuickBase(), c.Tenant, c.Encryption, c.PowerBase());
        var done = await state.DoneChunksAsync(runId, ct);
        var allPassed = contexts.Count > 0;
        foreach (var (tableMapId, ctx) in contexts)
        {
            var ranges = done.Where(d => d.TableMapId == tableMapId).Select(d => new QbRange(d.FromRecordId, d.ToRecordId)).ToList();
            // Issues logged before this moment belong to earlier validations; the report shows them as superseded.
            var startedOn = DateTime.UtcNow.AddSeconds(-1);
            var result = await validator.CompareTableAsync(ctx, ranges, sample, ct);
            await state.SaveValidationAsync(tableMapId, startedOn, result.Compared, result.Mismatches + result.Missing + result.Extra, result.NotEncrypted, ct);
            var orphans = await validator.OrphanReferencesAsync(run.PbAppId, ctx.PbTable.Id, ct);
            await state.AddIssuesAsync(runId, tableMapId, null, [.. result.Issues, .. orphans], ct);
            var passed = result.Gate3Passed && result.Gate7Passed && orphans.Count == 0;
            allPassed &= passed;
            await state.SetTableStatusAsync(tableMapId, passed ? "validated" : "loaded", ct);
            _log.LogInformation("{Table}: compared {Compared}/{Records} records; gate 3 {G3} ({Mismatch} differ, {Missing} missing, {Extra} extra); gate 4 {G4} ({Orphans} orphans); gate 7 {G7} ({Plain} not encrypted)",
                ctx.PbTable.Name, result.Compared, result.Records, result.Gate3Passed ? "pass" : "FAIL", result.Mismatches, result.Missing, result.Extra,
                orphans.Count == 0 ? "pass" : "FAIL", orphans.Count, result.Gate7Passed ? "pass" : "FAIL", result.NotEncrypted);
        }
        await state.SetRunStatusAsync(runId, allPassed ? "validated" : "loaded", ct);
        _log.LogInformation(allPassed ? "All gates passed." : "Some gates failed; see qbm.Issue (codes QBM_GATE*).");
        return allPassed;
    }

    /// <summary>Puts the migrated records into search through PowerBase's own admin backfill endpoint (POST admin/search/backfill),
    /// one call per table. Needs the PowerBase API URL and a super admin's user token (PowerBase:ApiToken, never in appsettings).</summary>
    public async Task ReindexAsync(long runId, string apiUrl, CancellationToken ct)
    {
        var token = c.Config["PowerBase:ApiToken"];
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("PowerBase:ApiToken (a super admin's user token) is required.");
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        foreach (var t in await c.State().TableMapsAsync(runId, ct))
        {
            if (t.Status is not ("loaded" or "validated")) continue;
            using var response = await http.PostAsync($"admin/search/backfill?tableId={t.PbTableId}&tenantId={c.TenantId}", null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Backfill for table {t.PbTableId} failed: {(int)response.StatusCode} {body}");
            _log.LogInformation("{Table}: search backfill started ({Status}); check GET admin/search/backfill/status", t.QbTableName, (int)response.StatusCode);
        }
    }

    // ---------- Operator commands: migrate, pause / resume / stop, status, retry-dlq, report ----------

    public const int ExitOk = 0, ExitGatesFailed = 2, ExitStopped = 4;

    /// <summary>The whole migration of one app in one command: map (first time only) → plan → load → finalize → validate →
    /// report. Running it again continues the same run where it ended (done chunks are skipped). Pause waits between chunks
    /// and between steps; stop ends after the chunks in progress, and the report is still written.</summary>
    public async Task<int> MigrateAsync(long? runId, string? qbAppId, long? pbAppId, long? runAsUserId, bool newRun, string queue, double sample,
        string? reportOut, CancellationToken ct)
    {
        var state = c.State();
        if (runId is null)
        {
            if (qbAppId is null || pbAppId is null) throw new ArgumentException("Give --run <id>, or --qb-app <dbid> --pb-app <id> (and --run-as <userId> for a new run).");
            runId = newRun ? null : await state.LatestRunAsync(qbAppId, pbAppId.Value, ct);
            if (runId is null)
            {
                if (runAsUserId is null) throw new ArgumentException("--run-as <userId> is required to start a new run.");
                _log.LogInformation("Step: map (new run)");
                runId = await MapAsync(qbAppId, pbAppId.Value, runAsUserId.Value, ct);
            }
            else _log.LogInformation("Continuing run {Run} (add --new to start a fresh run)", runId);
        }
        var id = runId.Value;
        await using var runLock = await LockAsync(id, ct);
        await ClearStopAsync(id, ct);
        var control = Control(id);

        var result = await StepsAsync();
        await TryReportAsync(id, reportOut, ct);
        _log.LogInformation("Run {Run}: {Outcome}. Status: 'status --run {Run}'.", id,
            result switch { ExitOk => "finished, all gates passed", ExitStopped => "stopped by an operator", _ => "finished with problems (see the report)" }, id);
        return result;

        async Task<int> StepsAsync()
        {
            if (!await NextAsync("plan")) return ExitStopped;
            await PlanAsync(id, extend: false, ct);
            if (!await NextAsync("load")) return ExitStopped;
            var complete = await LoadAsync(id, queue, reload: false, control, ct);
            if (control.Stopped) return ExitStopped;
            if (!complete)
            {
                _log.LogError("Run {Run}: some chunks failed; see 'status --run {Run}' and 'retry-dlq --run {Run}'", id, id, id);
                return ExitGatesFailed;
            }
            if (!await NextAsync("finalize")) return ExitStopped;
            var ok = await FinalizeCoreAsync(id, ct);
            if (!await NextAsync("validate")) return ExitStopped;
            ok &= await ValidateCoreAsync(id, sample, ct);
            return ok ? ExitOk : ExitGatesFailed;
        }

        async Task<bool> NextAsync(string step)
        {
            if (!await control.WaitUntilAllowedAsync(ct))
            {
                _log.LogWarning("Run {Run}: stopped before step {Step}", id, step);
                return false;
            }
            _log.LogInformation("Step: {Step}", step);
            return true;
        }
    }

    /// <summary>pause | resume | stop: sets the operator flag the running worker reads between chunks.</summary>
    public async Task SetControlAsync(long runId, string command, CancellationToken ct)
    {
        var state = c.State();
        await state.RunAsync(runId, ct);
        await state.SetControlAsync(runId, command == "resume" ? null : command, ct);
        _log.LogInformation(command switch
        {
            "pause" => "Run {Run}: pause requested. The worker finishes the chunks in progress (within a minute or two), then waits. 'resume' continues.",
            "stop" => "Run {Run}: stop requested. The worker finishes the chunks in progress, then exits. Start again with 'migrate --run {Run}'.",
            _ => "Run {Run}: resumed.",
        }, runId, runId);
    }

    /// <summary>Progress of a run: tables, records, chunks, speed, dead letters, and the chunks to look at first.</summary>
    public async Task StatusAsync(long runId, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        var p = await state.ProgressAsync(runId, ct);
        var busy = await state.TryLockRunAsync(runId, ct) is { } probe ? await ReleaseAsync(probe) : true;
        _log.LogInformation("Run {Run}: QuickBase app {Qb} -> PowerBase app {Pb}; status {Status}{Control}; worker {Worker}",
            runId, run.QbAppId, run.PbAppId, p.Status, p.Control is { } ctl ? $", {ctl} requested" : "", busy ? "running" : "not running");
        _log.LogInformation("Tables: {Loaded} of {Tables} loaded, {Blocked} blocked", p.TablesLoaded, p.Tables, p.TablesBlocked);
        _log.LogInformation("Records: {Written:N0} of {Total:N0} ({Pct:P1})", p.RecordsWritten, p.QbRecords, p.QbRecords == 0 ? 1 : (double)p.RecordsWritten / p.QbRecords);
        _log.LogInformation("Chunks: {Done} done, {Running} running, {Pending} pending, {Failed} failed (of {All})", p.ChunksDone, p.ChunksRunning, p.ChunksPending, p.ChunksFailed, p.Chunks);
        if (p.RecordsLast10Minutes > 0 && p.ChunksPending + p.ChunksRunning > 0)
        {
            var perSecond = p.RecordsLast10Minutes / 600.0;
            var left = Math.Max(0, p.QbRecords - p.RecordsWritten);
            _log.LogInformation("Speed (last 10 min): {Rate:N0} records/s; about {Minutes:N0} min left", perSecond, left / perSecond / 60);
        }
        var dead = await PeekDeadLettersAsync(ct);
        var ours = dead.Where(d => d.RunId == runId).ToList();
        _log.LogInformation("Dead-letter queue: {Ours} message(s) for this run{Others}", ours.Count, dead.Count > ours.Count ? $", {dead.Count - ours.Count} for other runs" : "");
        await FixFirstAsync(runId, ours, ct);
    }

    private static async Task<bool> ReleaseAsync(IAsyncDisposable probe)
    {
        await probe.DisposeAsync();
        return false;
    }

    /// <summary>The chunks a person should look at first: failed, or given up on by the queue, with their last error.</summary>
    private async Task FixFirstAsync(long runId, IReadOnlyList<QbmReport.DeadLetter> deadLetters, CancellationToken ct)
    {
        var state = c.State();
        var notDone = (await state.ChunkDetailsAsync(runId, null, ct)).Where(ch => ch.Status == "failed").ToList();
        var deadIds = deadLetters.Select(d => d.ChunkId).Except(notDone.Select(n => n.Id)).ToList();
        var all = notDone.Concat(deadIds.Count > 0 ? await state.ChunkDetailsAsync(runId, deadIds, ct) : []).ToList();
        if (all.Count == 0) { _log.LogInformation("Fix first: nothing; no failed chunks."); return; }
        _log.LogWarning("Fix first ({Count} chunk(s)):", all.Count);
        foreach (var ch in all)
        {
            var dl = deadLetters.FirstOrDefault(d => d.ChunkId == ch.Id);
            if (ch.Status == "done")
            {
                _log.LogInformation("  chunk {Chunk} {Table} Record IDs {From}..{To}: loaded since; its dead-letter message is a leftover ('retry-dlq --run {Run} --no-continue' removes it)",
                    ch.Id, ch.QbTableName, ch.FromRecordId, ch.ToRecordId - 1, runId);
                continue;
            }
            _log.LogWarning("  chunk {Chunk} {Table} Record IDs {From}..{To}: {Status}, {Attempts} attempt(s){Dead}. {Error}",
                ch.Id, ch.QbTableName, ch.FromRecordId, ch.ToRecordId - 1, ch.Status, ch.Attempts,
                dl is null ? "" : ", in the dead-letter queue", ch.LastError ?? dl?.Description ?? "");
        }
    }

    private async Task<List<QbmReport.DeadLetter>> PeekDeadLettersAsync(CancellationToken ct)
    {
        var connection = c.Config["ServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) return [];
        await using var client = new ServiceBusClient(connection);
        await using var receiver = client.CreateReceiver(QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var result = new List<QbmReport.DeadLetter>();
        long? from = null;
        while (true)
        {
            var batch = await receiver.PeekMessagesAsync(250, from, ct);
            if (batch.Count == 0) break;
            result.AddRange(batch.Select(m => new QbmReport.DeadLetter(RunIdOf(m), ChunkIdOf(m), null, m.DeadLetterReason, m.DeadLetterErrorDescription, m.EnqueuedTime.UtcDateTime)));
            from = batch[^1].SequenceNumber + 1;
        }
        return result;
    }

    /// <summary>Retries the chunks the queue gave up on: lists them (and every failed chunk) with the error, puts them back to
    /// pending, removes their dead-letter messages, then continues the migration (load → finalize → validate → report).
    /// --list only shows them. Fix the cause first: a chunk that fails for the same reason ends up dead-lettered again.</summary>
    public async Task<int> RetryDeadLettersAsync(long runId, bool listOnly, IReadOnlyCollection<long>? onlyChunks, bool continueRun, string queue, double sample,
        string? reportOut, CancellationToken ct)
    {
        var state = c.State();
        var dead = (await PeekDeadLettersAsync(ct)).Where(d => d.RunId == runId && (onlyChunks is null || onlyChunks.Contains(d.ChunkId))).ToList();
        await FixFirstAsync(runId, dead, ct);
        if (listOnly) return ExitOk;

        var failed = (await state.ChunkDetailsAsync(runId, null, ct))
            .Where(ch => ch.Status == "failed" && (onlyChunks is null || onlyChunks.Contains(ch.Id))).Select(ch => ch.Id);
        var retry = dead.Select(d => d.ChunkId).Concat(failed).Distinct().ToList();
        await using (var runLock = await LockAsync(runId, ct))
        {
            var reset = await state.ResetChunksAsync(retry, ct);
            var removed = await RemoveDeadLettersAsync(runId, dead.Select(d => d.ChunkId).ToHashSet(), ct);
            _log.LogInformation("Run {Run}: {Reset} chunk(s) set back to pending, {Removed} dead-letter message(s) removed", runId, reset, removed);
        }
        if (!continueRun)
        {
            _log.LogInformation("Load them with 'migrate --run {Run}'.", runId);
            return ExitOk;
        }
        return await MigrateAsync(runId, null, null, null, false, queue, sample, reportOut, ct);
    }

    private async Task<int> RemoveDeadLettersAsync(long runId, IReadOnlySet<long> chunkIds, CancellationToken ct)
    {
        var connection = c.Config["ServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection) || chunkIds.Count == 0) return 0;
        await using var client = new ServiceBusClient(connection);
        await using var receiver = client.CreateReceiver(QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var removed = 0;
        var seen = new HashSet<long>();
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(3), ct);
            if (batch.Count == 0) break;
            var progress = false;
            foreach (var m in batch)
            {
                if (RunIdOf(m) == runId && chunkIds.Contains(ChunkIdOf(m))) { await receiver.CompleteMessageAsync(m, ct); removed++; progress = true; }
                else
                {
                    await receiver.AbandonMessageAsync(m, cancellationToken: ct);
                    progress |= seen.Add(m.SequenceNumber);
                }
            }
            if (!progress) break; // only messages we leave alone are coming back
        }
        return removed;
    }

    /// <summary>Writes the Excel report (all runs of this app) and a short summary to the log. Returns the file path.</summary>
    public async Task<string> ReportAsync(long runId, string? output, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        var dead = (await PeekDeadLettersAsync(ct)).ToList();
        var collector = new QbmReportCollector(c.Tenant, c.Control, c.QuickBase());
        var report = await collector.CollectAsync(runId, c.TenantId, [], c.Config.GetValue("Migration:ReportRowCap", 200_000), ct);
        var tableByChunk = report.Chunks.ToDictionary(ch => ch.Id, ch => ch.Table);
        var runIds = report.Runs.Select(r => r.Id).ToHashSet();
        report = report with
        {
            DeadLetters = dead.Where(d => runIds.Contains(d.RunId)).Select(d => d with { Table = tableByChunk.GetValueOrDefault(d.ChunkId) }).ToList(),
        };

        var path = ReportPath(output, report, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = File.Create(path))
            QbmReportWriter.Write(report, file);

        var qb = report.Tables.Where(t => t.Status != "skipped").Sum(t => t.QbRecords ?? 0);
        var pb = report.Tables.Where(t => t.Status != "skipped").Sum(t => t.PbRecords);
        _log.LogInformation("Report for {App} (runs {Runs}): {Pb:N0} of {Qb:N0} records ({Pct:P2}), {Tables} tables, {Broken:N0} broken links, {Dead} dead letters",
            report.AppName, string.Join(", ", runIds), pb, qb, qb == 0 ? 1 : (double)pb / qb, report.Tables.Count, report.BrokenLinkTotal, report.DeadLetters.Count);
        _log.LogInformation("Report written: {Path}", path);
        await UploadReportAsync(path, ct);
        return path;
    }

    /// <summary>Copies the report to the private "reports" container of the migration's storage account, where an operator
    /// downloads it from the Azure Portal. Optional: without Migration:ReportStorage:ConnectionString (vault secret
    /// Migration--ReportStorage--ConnectionString) the report stays on this machine only. A failed upload never fails the run.</summary>
    private async Task UploadReportAsync(string path, CancellationToken ct)
    {
        var connection = c.Config["Migration:ReportStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            _log.LogInformation("Report not uploaded: no report storage configured (Migration:ReportStorage:ConnectionString); it is on this machine only");
            return;
        }
        try
        {
            var container = new BlobContainerClient(connection, c.Config["Migration:ReportStorage:Container"] ?? "reports");
            var blob = container.GetBlobClient(Path.GetFileName(path));
            await using var file = File.OpenRead(path);
            await blob.UploadAsync(file, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            }, ct);
            _log.LogInformation("Report uploaded: Azure Portal > storage account {Account} > Containers > {Container} > {Name}", container.AccountName, container.Name, blob.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Report upload failed ({Error}); the report is still at {Path}", ex.Message, path);
        }
    }

    private async Task TryReportAsync(long runId, string? output, CancellationToken ct)
    {
        try { await ReportAsync(runId, output, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Run {Run}: the report could not be written; run 'report --run {Run}' again", runId, runId);
        }
    }

    /// <summary>--out may be a file (.xlsx) or a folder; default folder: Migration:ReportDirectory, else ./reports.</summary>
    private string ReportPath(string? output, QbmReport report, long runId)
    {
        var name = $"{Safe(report.AppName)}-run{runId}-{report.GeneratedOn:yyyyMMdd-HHmm}.xlsx";
        if (output is { Length: > 0 } && output.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(output);
        var folder = output is { Length: > 0 } ? output : c.Config["Migration:ReportDirectory"] is { Length: > 0 } configured ? configured : "reports";
        return Path.GetFullPath(Path.Combine(folder, name));
    }

    private static string Safe(string name) =>
        new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-') is { Length: > 0 } s ? s : "app";
}
