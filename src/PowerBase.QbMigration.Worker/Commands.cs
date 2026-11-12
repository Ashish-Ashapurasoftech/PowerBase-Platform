using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
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
        var path = Path.Combine(AppContext.BaseDirectory, "sql", "001_qbm_state.sql");
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
        foreach (var t in await state.TableMapsAsync(runId, ct))
        {
            if (t.Status != "mapped") { _log.LogInformation("{Table}: {Status}, not planned", t.QbTableName, t.Status); continue; }
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
    public async Task RunAsync(long runId, string queue, bool reload, CancellationToken ct)
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
            // Tier by tier, like the local queue: a child matched to parents by key starts only after its parents are in.
            foreach (var tier in pending.GroupBy(p => TierOf(contexts[p.TableMapId])).OrderBy(g => g.Key))
                await RunServiceBusAsync(runId, tier.ToList(), contexts, processor, workers, ct);
        else await RunLocalAsync(pending, contexts, processor, workers, ct);

        var left = await state.PendingChunksAsync(runId, ct);
        _log.LogInformation("Run {Run}: {Left} chunks not done", runId, left.Count);
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
        QbmChunkProcessor processor, int workers, CancellationToken ct)
    {
        // Tier by tier: a child that matches parents by key must not start before its parents are in.
        foreach (var tier in pending.GroupBy(p => contexts[p.TableMapId]).GroupBy(g => TierOf(g.Key)).OrderBy(g => g.Key))
        {
            var channel = Channel.CreateUnbounded<QbmStateStore.ChunkRow>();
            foreach (var chunk in tier.SelectMany(g => g)) channel.Writer.TryWrite(chunk);
            channel.Writer.Complete();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                {
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

    private async Task RunServiceBusAsync(long runId, IReadOnlyList<QbmStateStore.ChunkRow> pending, Dictionary<long, QbmTableContext> contexts,
        QbmChunkProcessor processor, int workers, CancellationToken ct)
    {
        var connection = c.Config["ServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ServiceBus:ConnectionString is required for --queue servicebus.");
        var queueName = c.Config["ServiceBus:QueueName"] ?? "qb-migration-chunks";
        await using var client = new ServiceBusClient(connection);

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
            var body = JsonDocument.Parse(args.Message.Body.ToString()).RootElement;
            var chunkId = body.GetProperty("chunkId").GetInt64();
            if (body.GetProperty("runId").GetInt64() != runId || !byId.TryGetValue(chunkId, out var chunk))
            {
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken); // another run's message
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
        if (pending.Count > 0) await finished.Task.WaitAsync(ct);
        await consumer.StopProcessingAsync(CancellationToken.None);
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
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        var contexts = await ContextsAsync(run, ct, ["loaded", "validated"]);
        var validator = new QbmValidator(c.QuickBase(), c.Tenant, c.Encryption, c.PowerBase());
        var done = await state.DoneChunksAsync(runId, ct);
        var allPassed = contexts.Count > 0;
        foreach (var (tableMapId, ctx) in contexts)
        {
            var ranges = done.Where(d => d.TableMapId == tableMapId).Select(d => new QbRange(d.FromRecordId, d.ToRecordId)).ToList();
            var result = await validator.CompareTableAsync(ctx, ranges, sample, ct);
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

    public async Task ReportAsync(long runId, CancellationToken ct)
    {
        var state = c.State();
        var run = await state.RunAsync(runId, ct);
        _log.LogInformation("Run {Run}: QB app {Qb} -> PB app {Pb}, status {Status}", run.Id, run.QbAppId, run.PbAppId, run.Status);
        foreach (var t in await state.TableMapsAsync(runId, ct))
            _log.LogInformation("  tier {Tier} {Table} -> t_{Pb}: {Status}, QB {Count} records", t.Tier, t.QbTableName, t.PbTableId, t.Status, t.QbRecordCount);
        _log.LogInformation("  chunks not done: {Left}", (await state.PendingChunksAsync(runId, ct)).Count);
        foreach (var i in await state.IssueSummaryAsync(runId, ct))
            _log.LogInformation("  {Severity} {Code}: {Count}", i.Severity, i.Code, i.Count);
    }
}
