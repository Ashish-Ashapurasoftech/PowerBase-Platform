using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>Where the time of a record loop goes, per batch: counters filled by the loop workers and read when the batch ends.
/// Diagnostic only (one line in the log per batch).</summary>
internal static class LoopBatchProfile
{
    internal static long Items, ItemTicks, PreStepTicks, StepTicks, FirstChanceExceptions;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ExceptionTypes = new();
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> PhaseTicks = new();
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ExceptionStacks = new();

    /// <summary>Adds the time since <paramref name="last"/> to the named phase and moves the mark.</summary>
    internal static void Phase(string name, ref long last)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - last;
        PhaseTicks.AddOrUpdate(name, elapsed, (_, total) => total + elapsed);
        last = now;
    }

    internal static void Reset()
    {
        Interlocked.Exchange(ref Items, 0); Interlocked.Exchange(ref ItemTicks, 0);
        Interlocked.Exchange(ref PreStepTicks, 0); Interlocked.Exchange(ref StepTicks, 0);
        Interlocked.Exchange(ref FirstChanceExceptions, 0); ExceptionTypes.Clear(); PhaseTicks.Clear(); ExceptionStacks.Clear();
    }

    internal static void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        Interlocked.Increment(ref FirstChanceExceptions);
        var type = e.Exception.GetType().Name;
        ExceptionTypes.AddOrUpdate(type, 1, (_, count) => count + 1);
        if (!ExceptionStacks.ContainsKey(type))
        {
            var frames = new StackTrace(1, false).GetFrames()
                .Select(f => f.GetMethod()).Where(m => m?.DeclaringType != null)
                .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}")
                .Where(n => !n.StartsWith("AsyncMethodBuilder") && !n.Contains("MoveNext") || n.Contains("PowerBase"))
                .Take(14);
            ExceptionStacks.TryAdd(type, e.Exception.Message.ReplaceLineEndings(" ") + " @ " + string.Join(" <- ", frames));
        }
    }

    internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    internal static string Summary()
    {
        var items = Math.Max(1, Interlocked.Read(ref Items));
        var item = Ms(Interlocked.Read(ref ItemTicks)) / items;
        var pre = Ms(Interlocked.Read(ref PreStepTicks)) / items;
        var step = Ms(Interlocked.Read(ref StepTicks)) / items;
        var types = string.Join(", ", ExceptionTypes.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} x{kv.Value}"));
        var phases = string.Join(", ", PhaseTicks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {Ms(kv.Value) / items:F1}"));
        var stacks = string.Join(" || ", ExceptionStacks.Select(kv => $"{kv.Key}: {kv.Value}"));
        return $"per record: {item:F1} ms in total = {pre:F1} before the step + {step:F1} in the step + {item - pre - step:F1} after; " +
               $"first-chance exceptions: {Interlocked.Read(ref FirstChanceExceptions)}{(types.Length > 0 ? " (" + types + ")" : "")}; " +
               $"create-record phases (ms/record): {phases}; exception origin: {stacks}";
    }
}

public partial class PipelineEngine
{
    private Dictionary<(IAppTableRepository, IAppFieldRepository, Guid), (AppTable, IReadOnlyList<AppField>)>? _loopMetadata;

    private async Task<(AppTable Table, IReadOnlyList<AppField> Fields)> GetRecordStepMetadataAsync(
        IAppTableRepository tables, IAppFieldRepository fields, Guid tableId, CancellationToken ct)
    {
        var key = (tables, fields, tableId);
        if (_loopMetadata != null && _loopMetadata.TryGetValue(key, out var cached)) return cached;
        var table = await tables.GetByPublicIdAsync(tableId, ct);
        var result = (table, await fields.ListByTableAsync(table.Id, ct));
        if (_loopMetadata != null) _loopMetadata[key] = result;
        return result;
    }

    private sealed record LoopItem(object Value, int Index, string Path, long? StagingId = null);

    private async Task<string> ExecuteBatchedLoopAsync(
        PipelineStep step, LoopStepConfig config, Dictionary<string, object> context,
        List<PipelineStep> allSteps, Dictionary<string, object> steps, long runId,
        PipelineStepRun loopRun, List<RawStepAuditSnapshot> snapshots, string path,
        Guid messageId, CancellationToken ct)
    {
        if (config.MaxConcurrency is < 1 or > 32)
            throw new InvalidOperationException("Loop MaxConcurrency must be between 1 and 32.");

        var bulk = allSteps.Any(s => s.RefId == config.LoopOverStepId &&
            s.Type == "trigger" && s.Subtype == "new-bulk-event");
        steps.TryGetValue(config.LoopOverStepId!, out var source);
        var workset = TryGetChunkedSearchHandle(source, out var worksetId, out var searchTotal);
        if (bulk && messageId == Guid.Empty)
            throw new InvalidOperationException("BulkEventId (MessageId) is missing from the execution context.");

        var materialized = bulk || workset ? null : GetLoopCollection(source)?.ToList() ?? new List<object>();
        var total = workset ? searchTotal : materialized?.Count ?? 0;
        if (bulk && context.TryGetValue("trigger", out var trigger) &&
            trigger is Dictionary<string, object> triggerValues && triggerValues.TryGetValue("count", out var count))
            total = Convert.ToInt32(count);

        loopRun.InputContext = SerializeAndSanitizeAudit(new {
            LoopOverStepId = config.LoopOverStepId, TotalCount = total, ItemCount = total,
            BatchSize = _options.LoopBatchSize, IsBulkEvent = bulk, ChunkedSearch = workset
        });
        var hadPrevious = steps.TryGetValue(step.RefId, out var previous);
        var offset = 0;
        var batchNumber = 0;
        var processed = 0;
        var failed = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                List<LoopItem> items;
                if (bulk || workset)
                {
                    var rows = bulk
                        ? await _pipelineRepo.GetPendingBulkEventRecordsPageAsync(messageId, 1, _options.LoopBatchSize, ct)
                        : await _pipelineRepo.GetPendingSearchWorksetPageAsync(worksetId, _options.LoopBatchSize, ct);
                    items = rows.Select(r => new LoopItem(
                        bulk ? BuildBulkEventRecord(r) :
                            JsonSerializer.Deserialize<Dictionary<string, object?>>(r.AfterValuesJson ?? "{}") ?? new(),
                        r.Ordinal - 1,
                        bulk ? $"{path}/loop_index_{r.Ordinal}" : $"{path}/search_workset_{r.Ordinal}", r.Id)).ToList();
                }
                else
                {
                    items = materialized!.Skip(offset).Take(_options.LoopBatchSize)
                        .Select((value, i) => new LoopItem(value, offset + i, $"{path}/loop_index_{offset + i}")).ToList();
                    offset += items.Count;
                }
                if (items.Count == 0) break;
                batchNumber++;
                var concurrency = GetLoopBatchConcurrency(step, config, allSteps, items, total, context, steps);
                var batchRun = new PipelineStepRun {
                    PipelineRunId = runId, StepId = step.Id, Status = "Running", StartedOn = DateTime.UtcNow,
                    PipelineRunAttemptId = loopRun.PipelineRunAttemptId,
                    ExecutionPath = $"{path}/batch_{batchNumber}", SequenceNumber = NextStepSequence(context),
                    TransactionOutcome = "NotApplicable", StepPublicIdSnapshot = step.PublicId,
                    StepRefIdSnapshot = step.RefId, StepLabelSnapshot = $"Batch {batchNumber} · {items.Count} records",
                    StepTypeSnapshot = step.Type, StepSubtypeSnapshot = "loop-batch",
                    InputContext = JsonSerializer.Serialize(new { Batch = batchNumber, RecordCount = items.Count,
                        FirstRecord = items[0].Index + 1, LastRecord = items[^1].Index + 1, Workers = concurrency })
                };
                batchRun.Id = await _pipelineRepo.CreateStepRunAsync(batchRun, ct);
                var timer = Stopwatch.StartNew();
                var errors = new Exception?[items.Count];
                var completed = new bool[items.Count];
                LoopBatchProfile.Reset();
                AppDomain.CurrentDomain.FirstChanceException += LoopBatchProfile.OnFirstChance;
                try
                {
                    await ExecuteLoopBatchItemsAsync(step, allSteps, items, total, runId, context, steps,
                        snapshots, concurrency, false, completed, errors, ct);
                }
                finally
                {
                    AppDomain.CurrentDomain.FirstChanceException -= LoopBatchProfile.OnFirstChance;
                    _logger.LogInformation("Pipeline run {RunId}: Batch {Batch} profile — {Profile}", runId, batchNumber, LoopBatchProfile.Summary());
                    // The record action's receipt commits with its write. If a worker stops before
                    // this page checkpoint, replay skips its committed actions on the next attempt.
                    var successfulIds = items.Where((_, i) => completed[i]).Select(i => i.StagingId)
                        .Where(id => id.HasValue).Select(id => id!.Value).ToList();
                    if (successfulIds.Count > 0)
                    {
                        if (bulk) await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(successfulIds, 1, null, ct);
                        else if (workset) await _pipelineRepo.MarkSearchWorksetRecordsProcessedAsync(worksetId, successfulIds, 1, ct);
                    }
                    // A record whose own actions failed (a bad value, a rule that rejected it, …) is recorded as failed and
                    // SKIPPED: it stays in the history as a failed step, and the records after it are still processed. It is
                    // not retried — a retry would hit the same record first, every time, and block everything behind it.
                    // Only control flow / infrastructure errors (stop, pause, cancellation, deadlock) are left pending.
                    var failedIds = items.Where((_, i) => errors[i] != null && !IsControlFlowOrInfrastructureException(errors[i]!))
                        .Select(i => i.StagingId).Where(id => id.HasValue).Select(id => id!.Value).ToList();
                    if (failedIds.Count > 0)
                    {
                        if (bulk) await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(failedIds, LoopItemFailedAndSkipped, null, ct);
                        else if (workset) await _pipelineRepo.MarkSearchWorksetRecordsProcessedAsync(worksetId, failedIds, LoopItemFailedAndSkipped, ct);
                    }
                    processed += completed.Count(done => done);
                    failed += errors.Count(error => error != null);
                    // A Pause that releases the job is not a failure: the batch is simply waiting to continue.
                    var batchWaiting = errors.Any(error => error is PowerBase.Domain.Exceptions.PipelineWaitException);
                    var batchFailed = errors.Any(error => error != null && error is not PowerBase.Domain.Exceptions.PipelineWaitException);
                    batchRun.Status = batchFailed ? "Failed" : batchWaiting ? "Waiting"
                        : completed.Any(done => !done) ? "Failed" : "Success";
                    batchRun.CompletedOn = DateTime.UtcNow;
                    batchRun.OutputContext = JsonSerializer.Serialize(new {
                        Batch = batchNumber, RecordCount = items.Count, Succeeded = completed.Count(done => done),
                        Failed = errors.Count(error => error != null), ProcessedThisAttempt = processed,
                        TotalCount = total, Workers = concurrency, DurationMs = timer.ElapsedMilliseconds
                    });
                    batchRun.LogMessage = $"Batch {batchNumber}: {completed.Count(done => done)}/{items.Count} records succeeded in {timer.ElapsedMilliseconds} ms using {concurrency} worker(s).";
                    await _pipelineRepo.UpdateStepRunAsync(batchRun, ct);
                    _logger.LogInformation("Pipeline run {RunId}: {BatchSummary}", runId, batchRun.LogMessage);
                }
                var firstError = errors.FirstOrDefault(error => error != null && IsControlFlowOrInfrastructureException(error))
                    ?? errors.FirstOrDefault(error => error != null);
                // Ordinary record failures were recorded above and do not stop the loop; only control flow /
                // infrastructure errors (always preferred as `firstError`) end the attempt.
                if (firstError != null && IsControlFlowOrInfrastructureException(firstError))
                    ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }
        finally
        {
            if (hadPrevious) steps[step.RefId] = previous!;
            else steps.Remove(step.RefId);
        }
        return JsonSerializer.Serialize(new {
            LoopCompleted = true, IterationCount = bulk ? processed : total, FailedIterationCount = failed,
            ProcessedThisAttempt = processed, TotalCount = total, BatchCount = batchNumber,
            BatchSize = _options.LoopBatchSize, BulkLoop = bulk, ChunkedSearch = workset
        });
    }

    private int GetLoopBatchConcurrency(PipelineStep loop, LoopStepConfig config, List<PipelineStep> allSteps,
        List<LoopItem> items, int total, Dictionary<string, object> context, Dictionary<string, object> steps)
    {
        var capacity = _options.AutoScaleLoopWorkers
            ? _serviceProvider.GetRequiredService<PipelineLoopWorkerPool>().LoopConcurrency
            : _options.LoopConcurrency;
        var requested = Math.Min(config.MaxConcurrency ?? capacity, capacity);
        if (requested <= 1 || items.Count <= 1) return 1;
        var children = allSteps.Where(s => s.ParentStepId == loop.Id).OrderBy(s => s.DisplayOrder).ToList();
        // Only independent, flat record actions are automatic. Bulk-upsert sessions,
        // pauses, external requests and nested containers retain their ordering semantics.
        if (children.Count == 0 || children.Any(s => s.Subtype is not ("create-record" or "update-record" or "delete-record") ||
            !string.Equals(s.ParentBranch, "children", StringComparison.OrdinalIgnoreCase)) ||
            allSteps.Any(s => children.Any(child => child.Id == s.ParentStepId))) return 1;

        var unavailable = children.Select(s => s.RefId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            // A self/forward reference reads the preceding iteration in a sequential loop.
            // Dynamic indexing cannot be proven independent, so keep it sequential as well.
            var json = child.ConfigJson ?? "{}";
            if (Regex.IsMatch(json, @"\bsteps\s*\[", RegexOptions.IgnoreCase) ||
                unavailable.Any(reference => Regex.IsMatch(json,
                    $@"\b{Regex.Escape(reference)}\s*[.\[]", RegexOptions.IgnoreCase))) return 1;
            unavailable.Remove(child.RefId);
        }

        // Serialize batches containing duplicate update/delete destinations. Actions within
        // one iteration still run in order, including when they touch that same record.
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var child in children.Where(s => s.Subtype != "create-record"))
            {
                var mutation = JsonSerializer.Deserialize<UpdateRecordStepConfig>(child.ConfigJson ?? "{}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (mutation?.TargetRecordId == null) return 1;
                // Targets derived from another action are only known during execution.
                if (children.Any(action => Regex.IsMatch(mutation.TargetRecordId,
                    $@"\b{Regex.Escape(action.RefId)}\s*[.\[]", RegexOptions.IgnoreCase))) return 1;
                var localSteps = new Dictionary<string, object>(steps) { [loop.RefId] = LoopScope(items[i], total) };
                var localContext = new Dictionary<string, object>(context) { ["steps"] = localSteps };
                var target = EvaluateTokens(mutation.TargetRecordId, JsonSerializer.Serialize(localContext), items[i].Path, allSteps);
                if (!Guid.TryParse(target, out var recordId)) return 1;
                var key = $"{mutation.TableId}:{recordId}";
                if (targets.TryGetValue(key, out var prior) && prior != i) return 1;
                targets[key] = i;
            }
        }
        return Math.Min(requested, items.Count);
    }

    private static Dictionary<string, object> LoopScope(LoopItem item, int total) => new() {
        ["item"] = item.Value, ["index"] = item.Index,
        ["is_first"] = item.Index == 0, ["is_last"] = item.Index == total - 1
    };

    private async Task ExecuteLoopBatchItemsAsync(PipelineStep loop, List<PipelineStep> allSteps,
        List<LoopItem> items, int total, long runId, Dictionary<string, object> context,
        Dictionary<string, object> steps, List<RawStepAuditSnapshot> snapshots, int concurrency,
        bool stopOnError, bool[] completed, Exception?[] errors, CancellationToken ct)
    {
        if (concurrency == 1)
        {
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                steps[loop.RefId] = LoopScope(items[i], total);
                try
                {
                    await ExecuteSiblingStepsAsync(runId, allSteps, loop.Id, "children", context, steps,
                        snapshots, items[i].Path, ct);
                    completed[i] = true;
                }
                catch (Exception ex)
                {
                    errors[i] = ex;
                    if (stopOnError || IsControlFlowOrInfrastructureException(ex)) break;
                }
            }
            return;
        }

        var pool = _serviceProvider.GetRequiredService<PipelineLoopWorkerPool>();
        var childCount = allSteps.Count(s => s.ParentStepId == loop.Id);
        var sequenceBase = context.TryGetValue("_StepSequence", out var value) && value is int sequence ? sequence : 0;
        context["_StepSequence"] = checked(sequenceBase + items.Count * childCount);
        var localSnapshots = new List<RawStepAuditSnapshot>[items.Count];
        var outputs = new Dictionary<string, object>?[items.Count];
        var cursor = -1;
        var halt = 0;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ => {
                using var permit = await pool.AcquireAsync(ct);
                using var scope = _serviceScopeFactory.CreateScope();
                var query = scope.ServiceProvider.GetRequiredService<IQueryContext>();
                query.SetTenantId(_queryContext.TenantId);
                query.SetUserIdentity(_queryContext.UserId, _queryContext.IsSuperAdmin, _queryContext.UserName,
                    _queryContext.UserEmail, _queryContext.Permissions, _queryContext.TenantRole);
                query.SetTokenScope(_queryContext.IsUserToken, _queryContext.TokenAccessAllApps, _queryContext.AllowedAppIds);
                query.IsPipelineExecution = _queryContext.IsPipelineExecution;
                query.PipelineDepth = _queryContext.PipelineDepth;
                query.PipelineChainJson = _queryContext.PipelineChainJson;
                var engine = (PipelineEngine)scope.ServiceProvider.GetRequiredService<IPipelineEngine>();
                engine._loopMetadata = new();
                while (Volatile.Read(ref halt) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var i = Interlocked.Increment(ref cursor);
                    if (i >= items.Count) break;
                    // Eligible actions only replace step outputs; no shared mutable bulk sessions
                    // or containers are admitted. Each iteration gets its own writable dictionaries.
                    var localSteps = new Dictionary<string, object>(steps) { [loop.RefId] = LoopScope(items[i], total) };
                    var localContext = new Dictionary<string, object>(context) {
                        ["steps"] = localSteps, ["_StepSequence"] = sequenceBase + i * childCount
                    };
                    var audit = localSnapshots[i] = new List<RawStepAuditSnapshot>();
                    var itemStart = Stopwatch.GetTimestamp();
                    try
                    {
                        await engine.ExecuteSiblingStepsAsync(runId, allSteps, loop.Id, "children", localContext,
                            localSteps, audit, items[i].Path, ct);
                        completed[i] = true;
                        outputs[i] = localSteps;
                        Interlocked.Increment(ref LoopBatchProfile.Items);
                        Interlocked.Add(ref LoopBatchProfile.ItemTicks, Stopwatch.GetTimestamp() - itemStart);
                    }
                    catch (Exception ex)
                    {
                        errors[i] = ex;
                        if (stopOnError || IsControlFlowOrInfrastructureException(ex)) Interlocked.Exchange(ref halt, 1);
                    }
                }
            }));
        }
        finally
        {
            // Join before merging or returning: no worker may outlive its owning run/lease.
            foreach (var audit in localSnapshots) if (audit != null) snapshots.AddRange(audit);
            var last = outputs.LastOrDefault(output => output != null);
            if (last != null)
                foreach (var child in allSteps.Where(s => s.ParentStepId == loop.Id))
                    if (last.TryGetValue(child.RefId, out var output)) steps[child.RefId] = output;
        }
    }
}
