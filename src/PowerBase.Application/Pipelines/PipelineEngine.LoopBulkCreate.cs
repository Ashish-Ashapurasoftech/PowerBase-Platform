using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// A loop whose only step is Create Record, written a batch at a time. Record by record, every created record costs its own
/// transaction and some twenty database round trips (receipt checks, history insert and update, the active-flow check, the
/// encryption key, the insert, the id lookup, the read-back, the trigger lookup, the receipt, the commit…), which is what made a
/// million-record loop take more than a day. Here the batch's records are prepared exactly as the step prepares them one by one —
/// same mappings, references, defaults, Required / Unique / format rules and Custom Data Rule — and then written together: one
/// multi-row INSERT, one receipt write, one "record added" write and one history write, in one transaction.
/// </summary>
public partial class PipelineEngine
{
    /// <summary>What a bulk-written loop keeps from one batch to the next.</summary>
    private sealed class LoopBulkCreateState
    {
        /// <summary>Encrypted Unique fields: SQL cannot compare their values, and the single-record check decrypts the whole column for
        /// every record. Their values are read and decrypted once for the loop and grow with every committed batch.</summary>
        public readonly Dictionary<long, HashSet<string>> MemoryUnique = new();
        public int ConsecutiveFallbacks;
        public bool Disabled;
    }

    private sealed record BulkCreateRecord(int Position, Dictionary<long, object?> Values, Dictionary<string, object?> Mappings, DateTime StartedOn);

    /// <summary>Batches that could not be written in bulk this many times in a row (every one of them has a record a rule rejects, say)
    /// are not tried in bulk again for the rest of the loop: each try is wasted work before the record-by-record write.</summary>
    private const int BulkCreateMaxConsecutiveFallbacks = 3;

    /// <summary>Queues the search-index updates a step collected while it wrote, once its transaction has committed. A step's writes
    /// collect these messages instead of letting the repository publish them inside the transaction: a rollback would otherwise leave
    /// the index holding a record that was never written, or missing one whose delete was undone. Never fails the step.</summary>
    private void PublishCommittedIndexMessages(IReadOnlyCollection<SearchIndexMessage> messages, string source)
    {
        if (messages.Count == 0) return;
        var publisher = CurrentStepServices.GetService<IMessagePublisher>() ?? _serviceProvider.GetService<IMessagePublisher>();
        if (publisher == null) return;
        var count = messages.Count;
        _ = publisher.PublishBatchAsync(messages, CancellationToken.None).ContinueWith(
            t => _logger.LogWarning("Pipeline {Source}: could not queue the search-index updates of {Count} records: {Error}",
                source, count, t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>The loop's Create Record step when its batches can be written in bulk, otherwise null (and the reason is logged once).</summary>
    private async Task<PipelineStep?> GetLoopBulkCreateStepAsync(PipelineStep loop, LoopStepConfig config, List<PipelineStep> allSteps, CancellationToken ct)
    {
        if (!_options.BulkCreateRecordsInLoops) return null;
        var (create, reason) = await FindLoopBulkCreateStepAsync(loop, config, allSteps, ct);
        if (create == null && reason != null)
            _logger.LogInformation("Loop {LoopRefId} is written record by record, not in bulk: {Reason}.", loop.RefId, reason);
        return create;
    }

    private async Task<(PipelineStep? Create, string? Reason)> FindLoopBulkCreateStepAsync(
        PipelineStep loop, LoopStepConfig config, List<PipelineStep> allSteps, CancellationToken ct)
    {
        // An explicitly sequential loop asked for its records in order, one after another.
        if (config.MaxConcurrency == 1) return (null, "the loop is set to run one record at a time");
        var children = allSteps.Where(s => s.ParentStepId == loop.Id).ToList();
        if (children.Count != 1) return (null, "it has more than one step inside it");
        var create = children[0];
        if (create.Type != "action" || create.Subtype != "create-record") return (null, null);
        if (string.IsNullOrWhiteSpace(create.RefId) ||
            !string.Equals(create.ParentBranch, "children", StringComparison.OrdinalIgnoreCase) ||
            allSteps.Any(s => s.ParentStepId == create.Id)) return (null, "its Create Record step is not a plain step of the loop");

        // A mapping that reads the previous record's result, or indexes the steps dynamically, depends on the order of the records.
        var json = create.ConfigJson ?? "{}";
        if (Regex.IsMatch(json, @"\bsteps\s*\[", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(json, $@"\b{Regex.Escape(create.RefId)}\s*[.\[]", RegexOptions.IgnoreCase))
            return (null, "a mapping reads the previous record's result");

        // The step's account decides where it writes, exactly as when it runs on its own (ExecuteStepAsync): the flow's own
        // account (the builder saves it on every step) writes here; another tenant, or a saved account, runs in that account's
        // scope — record by record.
        Guid? connectionId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, "its configuration is not an object");
            foreach (var name in new[] { "connectionPublicId", "ConnectionPublicId" })
                if (document.RootElement.TryGetProperty(name, out var connection) && connection.ValueKind == JsonValueKind.String)
                {
                    if (Guid.TryParse(connection.GetString(), out var id)) connectionId = id;
                    break;
                }
        }
        catch (JsonException) { return (null, "its configuration cannot be read"); }
        if (connectionId is { } account && !PipelineStepValidator.SystemConnectionIds.Contains(account))
        {
            var tenantId = await _adminRepo.GetTenantIdByPublicIdAsync(account, ct);
            if (tenantId != _queryContext.TenantId)
                return (null, tenantId.HasValue ? "it writes into another account's tenant" : "it writes through a saved account");
        }
        return (create, null);
    }

    /// <summary>
    /// Writes one batch of a bulk-created loop. Returns false — having written and recorded nothing — when the batch has to be
    /// written record by record instead: a record a rule rejects (so that it fails on its own, with its own history, as before), two
    /// records of the batch taking the same Unique value, the flow switched off, or any error while preparing or writing.
    /// </summary>
    private async Task<bool> TryWriteLoopBatchInBulkAsync(
        PipelineStep loop, PipelineStep create, LoopBulkCreateState state, List<PipelineStep> allSteps, List<LoopItem> items,
        int total, long runId, Dictionary<string, object> context, Dictionary<string, object> steps, bool[] completed,
        CancellationToken ct)
    {
        if (state.Disabled) return false;
        var written = await WriteLoopBatchInBulkAsync(loop, create, state, allSteps, items, total, runId, context, steps, completed, ct);
        state.ConsecutiveFallbacks = written ? 0 : state.ConsecutiveFallbacks + 1;
        if (state.ConsecutiveFallbacks >= BulkCreateMaxConsecutiveFallbacks)
        {
            state.Disabled = true;
            _logger.LogInformation("Pipeline run {RunId}: loop {LoopRefId} could not be written in bulk {Count} batches in a row; it continues record by record.",
                runId, loop.RefId, state.ConsecutiveFallbacks);
        }
        return written;
    }

    private async Task<bool> WriteLoopBatchInBulkAsync(
        PipelineStep loop, PipelineStep create, LoopBulkCreateState state, List<PipelineStep> allSteps, List<LoopItem> items,
        int total, long runId, Dictionary<string, object> context, Dictionary<string, object> steps, bool[] completed,
        CancellationToken ct)
    {
        var messageId = context.TryGetValue("_MessageId", out var messageObj) && messageObj is Guid message ? message : Guid.Empty;
        if (messageId == Guid.Empty) return false;

        var paths = items.Select(item => $"{item.Path}/{create.RefId}").ToArray();
        var hashes = paths.Select(ComputeSha256Hash).ToArray();
        var sequenceBase = context.TryGetValue("_StepSequence", out var sequenceObj) && sequenceObj is int sequence ? sequence : 0;
        var outputs = new string?[items.Count];
        var batchStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var phase = batchStart;

        CreateRecordStepConfig config;
        AppTable table;
        IReadOnlyList<AppField> fields;
        string? dateFormat;
        var records = new List<BulkCreateRecord>(items.Count);
        try
        {
            // Once per batch instead of once per record: the record-by-record path stops the run with its usual message.
            var pipeline = await _pipelineRepo.GetByIdAsync(create.PipelineId, ct);
            if (pipeline == null || pipeline.IsDeleted || !pipeline.IsActive) return false;

            config = JsonSerializer.Deserialize<CreateRecordStepConfig>(create.ConfigJson ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (config == null || !Guid.TryParse(config.TableId, out var tableId)) return false;
            await EnforceStepAccessAsync(create, _serviceProvider, ct);
            (table, fields) = await GetRecordStepMetadataAsync(_tableRepo, _fieldRepo, tableId, ct);
            dateFormat = await PipelineCreateRecordRules.GetDateFormatAsync(table, CurrentStepServices, ct);

            // Records an earlier attempt already wrote keep their result (their receipt), exactly as a replay does record by record.
            var receipts = await _idempotencyRepo.GetManyByExecutionKeysAsync(messageId, create.PublicId, hashes, null, ct);
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (receipts.TryGetValue(Convert.ToHexString(hashes[i]), out var receipt)) { outputs[i] = receipt; continue; }

                // The same context the step sees when it runs for this record on its own.
                var localSteps = new Dictionary<string, object>(steps) { [loop.RefId] = LoopScope(items[i], total) };
                var localContext = new Dictionary<string, object>(context) { ["steps"] = localSteps, ["_StepSequence"] = sequenceBase + i + 1 };
                var payloadJson = JsonSerializer.Serialize(localContext);
                var startedOn = DateTime.UtcNow;
                await PrefetchUserTokenDataAsync(create.ConfigJson, payloadJson, paths[i], allSteps, ct);

                var values = new Dictionary<long, object?>();
                var mappings = new Dictionary<string, object?>();
                foreach (var mapping in config.FieldMappings ?? new List<FieldMapping>())
                {
                    if (string.IsNullOrWhiteSpace(mapping.Field)) continue;
                    var field = ResolvePipelineField(mapping.Field, fields);
                    if (!field.Fid.HasValue) continue;
                    var resolved = EvaluateTokens(await RenderUserTokensForTextAsync(mapping.Value, field, payloadJson, paths[i], allSteps, _fieldRepo, _tableRepo, ct),
                        payloadJson, paths[i], allSteps);
                    // Blank mappings are omitted, as the single-record step does.
                    if (string.IsNullOrWhiteSpace(resolved)) continue;
                    var parsed = ParseRecordMappingValueWithFormat(resolved, field, mapping.Value, mapping.FormatString);
                    values[field.Fid.Value] = parsed;
                    mappings[mapping.Field] = parsed;
                }
                await PipelineReferenceTargets.EnsureAvailableAsync(fields, values, _tableRepo, ct);
                foreach (var (fid, value) in await PowerBase.Application.Relationships.ReferenceWriteValidator.ValidateAsync(
                    fields, values, _tableRepo, _fieldRepo, _recordRepo, _relRepo, ct))
                    values[fid] = value;
                await PipelineCreateRecordRules.ApplyDefaultsAsync(fields, values, StepActingUserId, CurrentStepServices, ct);
                records.Add(new BulkCreateRecord(i, values, mappings, startedOn));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Pipeline run {RunId}: a batch of loop {LoopRefId} is written record by record: {Reason}", runId, loop.RefId, ex.Message);
            return false;
        }

        var uniqueFields = fields.Where(f => f.IsUnique && !f.IsSystem && f.Fid.HasValue &&
            !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !PhysicalNaming.IsRangeTypeCode(f.TypeCode)).ToList();
        var inBatch = uniqueFields.ToDictionary(f => (long)f.Fid!.Value, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var created = Array.Empty<Guid>() as IReadOnlyList<Guid>;
        var indexMessages = new List<SearchIndexMessage>();
        try
        {
            if (records.Count > 0)
            {
                LoopBatchProfile.Phase("bulk-prepare", ref phase);
                await _uow.BeginAsync(ct);
                // Unique fields: which of the batch's values the table already holds, asked once per field for the whole batch.
                var asked = new Dictionary<long, HashSet<string>>();
                var taken = new Dictionary<long, HashSet<string>>();
                foreach (var field in uniqueFields)
                {
                    var fid = (long)field.Fid!.Value;
                    if (state.MemoryUnique.ContainsKey(fid)) continue;
                    var candidates = new List<object>();
                    var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var record in records)
                        if (record.Values.TryGetValue(fid, out var candidate) && candidate != null &&
                            !PhysicalNaming.IsRequiredMissing(field.TypeCode, candidate) && candidateKeys.Add(UniqueValueKey(field, candidate, false)))
                            candidates.Add(candidate);
                    try
                    {
                        var found = candidates.Count == 0 ? new HashSet<int>()
                            : await _recordRepo.GetExistingValueIndexesAsync(table, field, candidates, _uow.Transaction, ct);
                        asked[fid] = candidateKeys;
                        taken[fid] = found.Select(index => UniqueValueKey(field, candidates[index], false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    catch (NotSupportedException)
                    {
                        // The app encrypts the field: read and decrypt its values once, for this batch and every later one.
                        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var stored in await _recordRepo.GetAllValuesForUniqueCheckAsync(table, field, null, ct)) held.Add(stored.Trim());
                        state.MemoryUnique[fid] = held;
                    }
                }
                bool? IsValueTaken(AppField field, object value)
                {
                    var fid = (long)field.Fid!.Value;
                    if (!inBatch.TryGetValue(fid, out var batchValues)) return null;
                    var memory = state.MemoryUnique.TryGetValue(fid, out var held);
                    var key = UniqueValueKey(field, value, memory);
                    if (batchValues.Contains(key)) return true;   // an earlier record of this batch takes it
                    if (memory) return held!.Contains(key);
                    // A value nobody asked the database about: let the database answer, as the single-record check does.
                    return asked.TryGetValue(fid, out var keys) && keys.Contains(key) ? taken[fid].Contains(key) : null;
                }
                LoopBatchProfile.Phase("bulk-unique-lookup", ref phase);
                foreach (var record in records)
                {
                    // A rejected record throws here: the batch is rolled back and written record by record, where it fails on its own.
                    await PipelineCreateRecordRules.ValidateAsync(table, fields, record.Values, _recordRepo, _tableRepo, _fieldRepo,
                        CurrentStepServices, _uow.Transaction, dateFormat, uniqueFields.Count > 0 ? IsValueTaken : null, ct);
                    foreach (var field in uniqueFields)
                        if (record.Values.TryGetValue(field.Fid!.Value, out var value) && value != null && !PhysicalNaming.IsRequiredMissing(field.TypeCode, value))
                            inBatch[field.Fid.Value].Add(UniqueValueKey(field, value, state.MemoryUnique.ContainsKey(field.Fid.Value)));
                }

                LoopBatchProfile.Phase("bulk-validate", ref phase);
                created = await _recordRepo.CreateManyAsync(table, fields,
                    records.Select(r => (IReadOnlyDictionary<long, object?>)r.Values).ToList(), _uow.Transaction!, ct, indexMessages.Add);
                if (created.Count != records.Count)
                    throw new InvalidOperationException("The batch insert returned an unexpected number of records.");

                LoopBatchProfile.Phase("bulk-insert", ref phase);
                var recordIds = new Dictionary<Guid, long>(created.Count);
                foreach (var chunk in created.Chunk(1000))
                    foreach (var (publicId, id) in await _recordRepo.GetActiveRecordIdsByPublicIdsAsync(table, chunk, _uow.Transaction, ct))
                        recordIds[publicId] = id;
                LoopBatchProfile.Phase("bulk-record-ids", ref phase);
                // Date Created / Record Owner are system-managed, so nothing submits them; filled in like the single-record step does.
                var createdOnField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedOn" && f.Fid.HasValue);
                var createdByField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
                var createdOn = DateTime.UtcNow;
                for (var n = 0; n < records.Count; n++)
                {
                    records[n].Values[3] = recordIds.TryGetValue(created[n], out var recordId)
                        ? recordId : throw new InvalidOperationException("A record of the batch insert is missing.");
                    if (createdOnField != null) records[n].Values[createdOnField.Fid!.Value] = createdOn;
                    if (createdByField != null) records[n].Values[createdByField.Fid!.Value] = StepActingUserId;
                }

                LoopBatchProfile.Phase("bulk-ids-backfill", ref phase);
                // The persisted rows (defaults, explicit nulls) and their Formula values, as the single-record step returns them.
                var persisted = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
                foreach (var chunk in recordIds.Values.Chunk(1000))
                    foreach (var (id, row) in await _recordRepo.GetBulkUpsertRowsByIdsAsync(table, fields, chunk, _uow.Transaction!, ct))
                        persisted[id] = row;
                var rows = records.Select(r => persisted.TryGetValue((long)r.Values[3]!, out var row) ? row : null).ToList();
                var present = rows.Where(row => row != null).Select(row => row!).ToList();
                var computed = present.Count > 0 ? await ProjectComputedAsync(table, fields, present, ct, inWriteTransaction: true) : null;
                for (int n = 0, p = 0; n < rows.Count; n++)
                    if (rows[n] != null && computed != null) rows[n] = MergeComputed(rows[n]!, computed[p++]);

                LoopBatchProfile.Phase("bulk-read-back", ref phase);
                var added = new List<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)>(records.Count);
                var receiptLogs = new List<PipelineStepIdempotencyLog>(records.Count);
                for (var n = 0; n < records.Count; n++)
                {
                    var output = BuildCreatedRecordOutput(created[n], (long)records[n].Values[3]!, fields, records[n].Values, rows[n]);
                    // The same complete snapshot the single-record step publishes for "record added".
                    added.Add((created[n], fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => output[$"fid_{f.Fid!.Value}"])));
                    var position = records[n].Position;
                    outputs[position] = JsonSerializer.Serialize(output);
                    receiptLogs.Add(new PipelineStepIdempotencyLog
                    {
                        MessageId = messageId, StepPublicId = create.PublicId, ExecutionPathHash = hashes[position],
                        ExecutionPath = paths[position], OutputJson = outputs[position]!
                    });
                }
                LoopBatchProfile.Phase("bulk-build-output", ref phase);
                if (await _triggerInterceptor.HasActiveSubscriptionsAsync(table, ct))
                    await _triggerInterceptor.InterceptAddedManyAsync(table, fields, added, ct);
                LoopBatchProfile.Phase("bulk-triggers", ref phase);
                await _idempotencyRepo.InsertManyAsync(receiptLogs, _uow.Transaction, ct);
                LoopBatchProfile.Phase("bulk-receipts", ref phase);
                await _uow.CommitAsync(ct);
                LoopBatchProfile.Phase("bulk-commit", ref phase);
            }
        }
        catch (Exception ex)
        {
            try { await _uow.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackEx) { _logger.LogWarning(rollbackEx, "Pipeline run {RunId}: rolling back a bulk loop batch failed.", runId); }
            if (ex is OperationCanceledException) throw;
            _logger.LogInformation("Pipeline run {RunId}: a batch of loop {LoopRefId} is written record by record: {Reason}", runId, loop.RefId, ex.Message);
            return false;
        }

        // Committed. Everything below is bookkeeping: it must never turn the written records into a failed (and retried) batch.
        PublishCommittedIndexMessages(indexMessages, $"loop {loop.RefId} of run {runId}");
        foreach (var (fid, held) in state.MemoryUnique)
            if (inBatch.TryGetValue(fid, out var batchValues)) held.UnionWith(batchValues);

        await WriteBulkCreateHistoryAsync(create, config, table, fields, records, outputs, paths, sequenceBase, runId, context, ct);
        phase = System.Diagnostics.Stopwatch.GetTimestamp();   // its two phases are counted inside
        Interlocked.Add(ref LoopBatchProfile.Items, records.Count);
        Interlocked.Add(ref LoopBatchProfile.ItemTicks, phase - batchStart);
        Interlocked.Add(ref LoopBatchProfile.StepTicks, phase - batchStart);
        context["_StepSequence"] = sequenceBase + items.Count;
        for (var i = 0; i < items.Count; i++) completed[i] = outputs[i] != null;
        var last = outputs.LastOrDefault(output => output != null);
        if (last != null)
        {
            try { steps[create.RefId] = JsonSerializer.Deserialize<object>(last) ?? last; }
            catch (JsonException) { steps[create.RefId] = last; }
        }
        return true;
    }

    /// <summary>One finished history row per written record — what the record-by-record path ends up with after its insert, its update
    /// and the run's final audit formatting — written in one go.</summary>
    private async Task WriteBulkCreateHistoryAsync(PipelineStep create, CreateRecordStepConfig config, AppTable table, IReadOnlyList<AppField> fields,
        List<BulkCreateRecord> records, string?[] outputs, string[] paths, int sequenceBase, long runId, Dictionary<string, object> context,
        CancellationToken ct)
    {
        if (records.Count == 0) return;
        var phase = System.Diagnostics.Stopwatch.GetTimestamp();
        var attemptId = context.TryGetValue("_RunAttemptId", out var attemptObj) && attemptObj is long attempt ? attempt : (long?)null;
        var correlationId = context.TryGetValue("_CorrelationId", out var correlationObj) ? correlationObj?.ToString() ?? string.Empty : string.Empty;
        var metadata = BuildAuditFieldMetadata(table, fields);
        var history = new List<PipelineStepRun>(records.Count);
        foreach (var record in records)
        {
            var output = outputs[record.Position]!;
            var rawInput = SerializeAndSanitizeAudit(new { TableId = config.TableId, FieldMappings = record.Mappings, Metadata = metadata });
            var completedOn = DateTime.UtcNow;
            var run = new PipelineStepRun
            {
                PipelineRunId = runId, StepId = create.Id, Status = "Success", StartedOn = record.StartedOn, CompletedOn = completedOn,
                InputContext = rawInput, OutputContext = SerializeAndSanitizeAuditString(output), LogMessage = "Step executed. Status: Success.",
                PipelineRunAttemptId = attemptId, ExecutionPath = paths[record.Position], SequenceNumber = sequenceBase + record.Position + 1,
                TransactionOutcome = "Committed", StepPublicIdSnapshot = create.PublicId, StepRefIdSnapshot = create.RefId,
                StepLabelSnapshot = !string.IsNullOrWhiteSpace(create.Label) ? create.Label : create.RefId,
                StepTypeSnapshot = create.Type, StepSubtypeSnapshot = create.Subtype
            };
            try
            {
                var (formattedInput, formattedOutput, logMessage) = _auditFormatter.FormatStepRun(
                    create, rawInput, output, "Success", correlationId, record.StartedOn, completedOn);
                run.InputContext = formattedInput ?? run.InputContext;
                run.OutputContext = formattedOutput ?? run.OutputContext;
                run.LogMessage = !string.IsNullOrEmpty(logMessage) ? logMessage : "Step execution finished with status Success.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Formatter failed for step {StepId} of run {RunId}. Proceeding with raw values.", create.Id, runId);
            }
            history.Add(run);
        }
        LoopBatchProfile.Phase("bulk-history-format", ref phase);
        try
        {
            using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            await _pipelineRepo.CreateCompletedStepRunsAsync(history, ct);
            suppress.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pipeline run {RunId}: {Count} records were created but their step history could not be written.", runId, history.Count);
        }
        LoopBatchProfile.Phase("bulk-history-write", ref phase);
    }

    /// <summary>How two Unique values compare: the stored text for an encrypted field (as its decrypted index holds it), otherwise the
    /// value as SQL compares it (a number by its value, text without trailing spaces), case-insensitively.</summary>
    private static string UniqueValueKey(AppField field, object value, bool decryptedIndex)
    {
        if (decryptedIndex) return (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        if (field.TypeCode == "Number" && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd() ?? string.Empty;
    }

    /// <summary>Formats and saves the history of the steps a finished loop batch recorded, and lets go of them. Held until the end of
    /// the run, a loop over a million records kept a million snapshots (with their inputs and outputs) in memory and then saved them one
    /// by one after the loop.</summary>
    private async Task FlushAuditSnapshotsAsync(List<RawStepAuditSnapshot> snapshots, int from, Dictionary<string, object> context,
        long runId, CancellationToken ct)
    {
        if (snapshots.Count <= from) return;
        var correlationId = context.TryGetValue("_CorrelationId", out var correlationObj) ? correlationObj?.ToString() ?? string.Empty : string.Empty;
        var finished = snapshots.GetRange(from, snapshots.Count - from);
        snapshots.RemoveRange(from, finished.Count);
        foreach (var snapshot in finished) await SaveFormattedAuditAsync(snapshot, correlationId, runId, ct);
    }

    private async Task SaveFormattedAuditAsync(RawStepAuditSnapshot snap, string correlationId, long runId, CancellationToken ct)
    {
        try
        {
            var (formattedInput, formattedOutput, logMsg) = _auditFormatter.FormatStepRun(
                snap.Step, snap.RawInputJson, snap.RawOutputJson, snap.Status, correlationId, snap.StartedOn, snap.CompletedOn);

            snap.StepRun.InputContext = formattedInput;
            snap.StepRun.OutputContext = formattedOutput;
            snap.StepRun.LogMessage = !string.IsNullOrEmpty(logMsg) ? logMsg : $"Step execution finished with status {snap.Status}.";
            snap.StepRun.CompletedOn = snap.CompletedOn;

            using (var suppressScope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                await _pipelineRepo.UpdateStepRunAsync(snap.StepRun, ct);
                suppressScope.Complete();
            }
        }
        catch (Exception fmtEx)
        {
            _logger.LogError(fmtEx, "Formatter failed for step {StepId} of run {RunId}. Proceeding with raw values.", snap.Step.Id, runId);
        }
    }
}
