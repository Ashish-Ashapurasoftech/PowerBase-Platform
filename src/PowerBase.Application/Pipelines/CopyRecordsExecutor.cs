using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Pipelines;

/// <summary>Copy-only execution. Snapshot and row receipts share the existing durable
/// idempotency store, so a retry cannot copy a different source view or repeat a write.</summary>
public sealed class CopyRecordsExecutor(IServiceProvider services)
{
    private sealed record Snapshot(int Pages, string WrappedKey, DateTime StartedUtc, string ConfigurationHash);
    private sealed record Receipt(bool Inserted, bool Updated, string? Error = null);
    private sealed class FastPageNotApplicableException : Exception { }
    private sealed record Summary(long InsertedCount, long UpdatedCount, long ErrorCount, List<string> Errors,
        List<CopyFailedRecord>? FailedRecords = null, bool FailedRecordsTruncated = false,
        int FailedRecordPages = 0, long FailedRecordsStored = 0);
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private async Task<object?> DefaultValue(AppField field, IQueryContext context, CancellationToken ct)
    {
        if (field.TypeCode == "Boolean") return field.DefaultValue!.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (field.TypeCode is not ("User" or "MultiUser")) return field.DefaultValue;
        try
        {
            using var doc = JsonDocument.Parse(field.DefaultValue!);
            var root = doc.RootElement;
            var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : null;
            long? id = mode == "CurrentUser" ? context.UserId : null;
            if (mode == "SpecificUser" && root.TryGetProperty("userPublicId", out var user) && Guid.TryParse(user.GetString(), out var publicId))
                id = await UserFieldValueResolver.TryResolveLongIdAsync(services.GetRequiredService<IUserRepository>(), publicId, ct);
            return id == null ? null : field.TypeCode == "MultiUser" ? JsonSerializer.Serialize(new[] { id.Value.ToString(CultureInfo.InvariantCulture) }) : id.Value;
        }
        catch (JsonException) { return null; }
    }

    /// <param name="onTablesLoaded">Called once the source and destination metadata are loaded, so the
    /// caller can store table and field names with the step's audit input (the tables may live in a
    /// tenant the audit formatter cannot read).</param>
    public async Task<string> ExecuteAsync(CopyRecordsDefinition config, string query, Guid stepId,
        Guid messageId, string executionPath, CancellationToken cancellationToken,
        Action<AppTable, IReadOnlyList<AppField>, AppTable, IReadOnlyList<AppField>>? onTablesLoaded = null)
    {
        config.ValidateShape();
        // No time limit: how long a copy takes depends on how many records there are.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = timeout.Token;
        var tableRepo = services.GetRequiredService<IAppTableRepository>();
        var fieldRepo = services.GetRequiredService<IAppFieldRepository>();
        var accessService = services.GetRequiredService<IAppAccessService>();
        var enforcer = services.GetRequiredService<IRolePermissionEnforcer>();
        var queryContext = services.GetRequiredService<IQueryContext>();
        var records = services.GetRequiredService<IRecordRepository>();
        var relRepo = services.GetService<IRelationshipRepository>();
        var writes = services.GetRequiredService<IRecordWriteService>();
        var search = services.GetRequiredService<IPipelineRecordSearchService>();
        var receipts = services.GetRequiredService<IPipelineStepIdempotencyRepository>();
        var uow = services.GetRequiredService<ITenantUnitOfWork>();
        var encryption = services.GetRequiredService<IEncryptionService>();
        var sourceId = CopyRecordsDefinition.TableId(config.SourceTable);
        var destinationId = CopyRecordsDefinition.TableId(config.DestinationTable);
        // Membership only: what the user may do with the records is decided by the table-level
        // access checks below (the flat records:* codes are not carried by regular roles).
        await accessService.RequireMembershipByTablePublicIdAsync(sourceId, ct);
        await accessService.RequireMembershipByTablePublicIdAsync(destinationId, ct);
        var source = await tableRepo.GetByPublicIdAsync(sourceId, ct);
        var destination = await tableRepo.GetByPublicIdAsync(destinationId, ct);
        var sourceFields = await fieldRepo.ListByTableAsync(source.Id, ct);
        var destinationFields = await fieldRepo.ListByTableAsync(destination.Id, ct);
        onTablesLoaded?.Invoke(source, sourceFields, destination, destinationFields);
        config.ValidateFields(sourceFields, destinationFields);
        var sourceAccess = await enforcer.GetTableAccessAsync(source, sourceFields, ct);
        var destinationAccess = await enforcer.GetTableAccessAsync(destination, destinationFields, ct);
        if (!sourceAccess.CanView) throw new UnauthorizedActionException("You cannot read the source table.");
        if (!destinationAccess.Unrestricted && (!destinationAccess.CanAdd || destinationAccess.ModifyScope == RecordScopes.None))
            throw new UnauthorizedActionException("You cannot copy records into the destination table.");
        var exported = config.SourceFields.Select(f => CopyRecordsDefinition.Field(f, sourceFields)).ToList();
        var imported = config.DestinationFields.Select(f => CopyRecordsDefinition.Field(f, destinationFields)).ToList();
        if (!sourceAccess.Unrestricted && exported.Any(f => !sourceAccess.VisibleFields.Any(v => v.Fid == f.Fid)))
            throw new UnauthorizedActionException("A source field is not visible to this account.");
        if (!destinationAccess.Unrestricted && imported.Any(f => !f.IsSystem && !destinationAccess.EditableFieldIds.Contains(f.Fid!.Value)))
            throw new UnauthorizedActionException("A destination field is not editable by this account.");
        var merge = CopyRecordsDefinition.Field(config.MergeField, destinationFields);
        var visibleSourceFields = sourceAccess.Unrestricted ? sourceFields : sourceAccess.VisibleFields;
        var filter = CopyRecordsDefinition.ParseQuery(query, visibleSourceFields);
        var effectiveFilter = new FilterGroup();
        if (filter != null) effectiveFilter.Nodes.Add(new() { Group = filter });
        if (sourceAccess.ViewFilter != null) effectiveFilter.Nodes.Add(new() { Group = sourceAccess.ViewFilter });
        if (sourceAccess.RestrictToCreatedBy.HasValue)
            effectiveFilter.Nodes.Add(new() { Condition = new() { FieldId = 4, Operator = "eq", Value = sourceAccess.RestrictToCreatedBy.Value.ToString(CultureInfo.InvariantCulture) } });

        // SQL Server accepts at most ~2100 parameters per query, so any Id-list lookup below
        // (AI Search match resolution, the source-row fetch itself) must be batched in chunks
        // this size — never trimmed to it. A trimmed cap would silently drop rows past the
        // cutoff from the copy; chunking fetches every one of them, just across more queries.
        const int sqlIdChunkSize = 2000;

        // Performance: when Azure AI Search grid-search is enabled (same UseAzureAiForGridSearch flag Reports and pipeline
        // Search Records use), find the candidate source records through the search index instead of scanning the source
        // table — but only when the index can answer the filter exactly (every condition on a plain-text physical field that
        // is both searchable and filterable; see PipelineAiSearchPlanner). Anything else is read from SQL. Every candidate id
        // is paged out of the index (no ceiling), and the candidates are then read from SQL with the COMPLETE filter applied
        // below, so only records SQL itself matches are copied. When the index finds nothing, or fails, the source is read
        // from SQL. Read-only and pre-transaction: this only narrows which rows the snapshot read selects, it never guards a write.
        List<long>? aiCandidateIds = null;
        var azureSearch = services.GetService<IAzureSearchService>();
        var copyLogger = services.GetService<ILogger<CopyRecordsExecutor>>();
        if (azureSearch != null && azureSearch.IsGridSearchEnabled)
        {
            var aiPlan = PipelineAiSearchPlanner.Evaluate(effectiveFilter, sourceFields);
            if (!aiPlan.UseAiSearch)
                copyLogger?.LogInformation("[CopyRecords] Source table {TableId} is read from SQL, not Azure AI Search: {Reason}.", source.Id, aiPlan.SqlReason);
            else if (await azureSearch.IsHealthyAsync(ct))
            {
                try
                {
                    var found = new List<long>();
                    await foreach (var page in azureSearch.SearchRecordIdsByFilterPagedAsync(queryContext.TenantId, source.Id, aiPlan.ODataFilter!, null, ct))
                        foreach (var publicIdChunk in page.Ids.Chunk(sqlIdChunkSize))
                            found.AddRange(await records.GetIdsByPublicIdsAsync(source, publicIdChunk, ct));
                    if (found.Count == 0)
                        copyLogger?.LogInformation("[CopyRecords] Azure AI Search found no matching record for table {TableId}; checking SQL.", source.Id);
                    else
                    {
                        found.Sort();   // the same order a SQL read would give
                        aiCandidateIds = found;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    copyLogger?.LogWarning(ex, "[CopyRecords] Azure AI Search unavailable for table {TableId}. Falling back to SQL.", source.Id);
                }
            }
        }

        var computedFids = sourceFields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode)).Select(f => (long)f.Fid!.Value).ToHashSet();
        // Encrypted fields DO have a physical column (unlike formula fields) — they still need
        // to be selected and decrypted normally. Only their filter *conditions* must be pulled
        // out of the SQL tree (ciphertext can't satisfy a LIKE/=), so this set is used solely for
        // the tree split below, never for the snapshotFields exclusion further down.
        var encryptedFilterFids = sourceFields.Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && f.IsEncrypted)
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        var inMemoryFilterFids = new HashSet<long>(computedFids);
        inMemoryFilterFids.UnionWith(encryptedFilterFids);
        // A Reference searched by its display text can't be matched in SQL (the column holds the
        // parent's Record ID#); id-style conditions on it stay in SQL.
        inMemoryFilterFids.UnionWith(PipelineComputedFilter.LabelStyleReferenceFids(sourceFields, effectiveFilter));
        var referenceFids = sourceFields.Where(f => f.Fid.HasValue && f.TypeCode == "Reference").Select(f => (long)f.Fid!.Value).ToHashSet();
        // Physical conditions go to SQL; computed/encrypted conditions are evaluated in memory against the decrypted /
        // projected rows. That is the same whether the rows come from a full scan or only from the candidates AI Search
        // found: the candidates are just one more SQL restriction (one chunk of ids at a time, so no row is dropped and
        // none copied twice) — their conditions are still applied in full.
        FilterGroup? physicalFilter;
        FilterGroup? computedFilter;
        (physicalFilter, computedFilter) = FormulaFilterSorter.SplitFilterTree(effectiveFilter, inMemoryFilterFids);
        List<FilterGroup?> physicalFilterChunks = aiCandidateIds == null
            ? new List<FilterGroup?> { physicalFilter }
            : aiCandidateIds.Chunk(PipelineEngine.AiCandidateRecordIdChunkSize).Select(idChunk => (FilterGroup?)PipelineEngine.WithIdRestriction(physicalFilter, idChunk, null)).ToList();

        // Each receipt namespace belongs to one logical step execution, including loop path.
        var prefix = executionPath + "/copy";
        var configurationHash = Convert.ToHexString(Hash(JsonSerializer.Serialize(new
        {
            config.SourceTable,
            config.DestinationTable,
            config.SourceFields,
            config.DestinationFields,
            config.MergeField,
            Query = query
        })));
        Task<string?> Read(string suffix) => receipts.GetByExecutionKeyAsync(messageId, stepId, Hash(prefix + suffix), uow.Transaction, ct);
        Task Store(string suffix, string json) => receipts.InsertAsync(new PipelineStepIdempotencyLog
        {
            MessageId = messageId,
            StepPublicId = stepId,
            ExecutionPath = prefix + suffix,
            ExecutionPathHash = Hash(prefix + suffix),
            OutputJson = json
        }, uow.Transaction, ct);
        string Finish(string json)
        {
            var summary = JsonSerializer.Deserialize<Summary>(json)!;
            if (summary.ErrorCount > 0 && config.TerminateOnError == "Yes")
                throw new PipelineNonRetryableException(
                    $"Copy Records: {summary.ErrorCount} {(summary.ErrorCount == 1 ? "record" : "records")} could not be copied ({summary.InsertedCount} inserted, {summary.UpdatedCount} updated). First error: {summary.Errors.FirstOrDefault()}",
                    new Dictionary<string, object?>
                    {
                        ["InsertedCount"] = summary.InsertedCount,
                        ["UpdatedCount"] = summary.UpdatedCount,
                        ["ErrorCount"] = summary.ErrorCount,
                        ["FailedRecords"] = summary.FailedRecords ?? new List<CopyFailedRecord>(),
                        ["FailedRecordsTruncated"] = summary.FailedRecordsTruncated,
                        ["FailedRecordPages"] = summary.FailedRecordPages,
                        ["FailedRecordsStored"] = summary.FailedRecordsStored
                    });
            return json;
        }
        var complete = await Read("/complete");
        if (complete != null) return Finish(complete);
        var manifestJson = await Read("/snapshot");
        Snapshot snapshot;
        if (manifestJson == null)
        {
            var wrappedKey = await encryption.GenerateAndWrapDekAsync(queryContext.TenantId, source.AppId, ct);
            var pages = 0;
            var started = DateTime.UtcNow;
            await uow.BeginAsync(ct);
            try
            {
                // All pages and the manifest become visible together. On interruption the
                // snapshot transaction rolls back before any destination writes begin.
                var needed = new HashSet<long>(exported.Select(f => (long)f.Fid!.Value));
                void IncludeFilter(FilterGroup group)
                {
                    foreach (var node in group.Nodes)
                    {
                        if (node.Condition != null)
                        {
                            needed.Add(node.Condition.FieldId);
                            if (node.Condition.ValueFieldId.HasValue) needed.Add(node.Condition.ValueFieldId.Value);
                        }
                        if (node.Group != null) IncludeFilter(node.Group);
                    }
                }
                IncludeFilter(effectiveFilter);
                var needsComputed = exported.Any(f => computedFids.Contains(f.Fid!.Value)) || computedFilter != null;
                var snapshotFields = sourceFields.Where(f => f.Fid.HasValue && !computedFids.Contains(f.Fid.Value) && (needsComputed || needed.Contains(f.Fid.Value))).ToList();
                // Only the computed values the copy uses are projected: the exported ones, the ones its filter reads, and what those
                // read in turn — never every Formula / Lookup / Summary / Reference label of the source. A Summary over an encrypted
                // child table is computed in memory over every child row, for every page: copying Order (6 million rows) while its
                // Order Detail summaries were projected scanned all of Order Detail 24,000 times, though the copy exported only
                // "ORD000" & [Record ID#]. The relationship projection runs only when one of those fields is a Lookup / Summary /
                // Reference, and then for those fields alone. A formula whose inputs cannot be listed up front (a field chosen per
                // record) still gets the full projection, as before.
                var neededComputedFids = exported
                    // An exported Reference is copied as its display text whenever the source has computed fields (see below).
                    .Where(f => computedFids.Contains(f.Fid!.Value) || (computedFids.Count > 0 && referenceFids.Contains(f.Fid!.Value)))
                    .Select(f => (long)f.Fid!.Value)
                    .Concat(PipelineComputedProjection.ReferencedFids(computedFilter).Where(fid => computedFids.Contains(fid) || referenceFids.Contains(fid)))
                    .ToHashSet();
                var formulaProjector = services.GetRequiredService<IFormulaProjector>();
                IReadOnlyList<AppField> projectedFields = sourceFields;
                var projectsRelationships = true;
                var neededFormulaFids = sourceFields
                    .Where(f => f.Fid.HasValue && neededComputedFids.Contains(f.Fid.Value) && FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings))
                    .Select(f => (long)f.Fid!.Value).ToList();
                var closure = neededFormulaFids.Count == 0
                    ? new HashSet<long>()
                    : formulaProjector.GetFormulaDependencies(sourceFields, neededFormulaFids)?.ToHashSet();
                // The dependency set always holds the formulas it started from; one that does not is not trusted.
                if (closure != null && neededFormulaFids.All(closure.Contains))
                {
                    closure.UnionWith(neededComputedFids);
                    // Stored fields (the formulas' inputs) plus the computed / Reference fields in the closure, nothing else.
                    projectedFields = sourceFields.Where(f => f.Fid.HasValue &&
                        (!(PhysicalNaming.IsComputedTypeCode(f.TypeCode) || f.TypeCode == "Reference") || closure.Contains(f.Fid.Value))).ToList();
                    projectsRelationships = projectedFields.Any(f => f.TypeCode is "Lookup" or "Summary" or "Reference");
                }
                // Normally one chunk (one query); multiple only when AI Search matched more rows
                // than a single SQL "IN" clause can parameterize — every chunk still feeds the
                // same page/receipt sequence below, so no row is dropped and none copied twice.
                foreach (var filterChunk in physicalFilterChunks)
                {
                await foreach (var page in search.ReadCopySnapshotAsync(source, snapshotFields, filterChunk, ct))
                {
                    IReadOnlyList<IReadOnlyDictionary<string, object?>> projected = page;
                    if (needsComputed)
                    {
                        // Nothing computed is needed (a filter on encrypted stored fields only): the decrypted rows are filtered as they are.
                        var projection = neededComputedFids.Count == 0 ? null : await PipelineComputedProjection.ProjectAsync(
                            projectsRelationships ? services.GetRequiredService<IRelationalProjector>() : null, formulaProjector,
                            source, projectedFields, page, services.GetService<ILogger<CopyRecordsExecutor>>(), ct);
                        // A field the copy exports or filters on that cannot be computed fails the copy with its name;
                        // any other broken field is simply not needed.
                        PipelineComputedProjection.ThrowIfNeeded(projection, sourceFields,
                            exported.Select(f => (long)f.Fid!.Value).Concat(PipelineComputedProjection.ReferencedFids(computedFilter)), "Copy Records");
                        IReadOnlyList<IReadOnlyDictionary<long, object?>> computed = projection?.Values ?? page.Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList();
                        var pairs = page.Select((row, i) => (Row: row, Computed: computed[i])).ToList();
                        if (computedFilter != null) pairs = PipelineComputedFilter.Apply(pairs, computedFilter, sourceFields);
                        projected = pairs.Select(pair =>
                        {
                            var row = new Dictionary<string, object?>(pair.Row);
                            foreach (var value in pair.Computed)
                            {
                                // The Reference display text was projected only to evaluate the filter:
                                // without computed fields the copy keeps the stored Record ID#.
                                if (computedFids.Count == 0 && referenceFids.Contains(value.Key)) continue;
                                row[PhysicalNaming.ColumnName((int)value.Key)] = value.Value;
                            }
                            return (IReadOnlyDictionary<string, object?>)row;
                        }).ToList();
                    }
                    if (projected.Count == 0) continue;
                    // Persist only exported values, not formula dependencies or hidden columns.
                    // "Id" (the source Record ID#) is kept only so a rejected record can be named in the run history.
                    var exportColumns = new HashSet<string>(exported.Select(PhysicalNaming.GetPhysicalColumnName), StringComparer.OrdinalIgnoreCase) { "PublicId", "Id" };
                    foreach (var range in exported.Where(f => PhysicalNaming.IsRangeTypeCode(f.TypeCode))) exportColumns.Add(PhysicalNaming.EndColumnName(range.Fid!.Value));
                    var json = JsonSerializer.Serialize(projected.Select(row => row.Where(p => exportColumns.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)));
                    var encrypted = await encryption.EncryptDataAsync(json, wrappedKey, queryContext.TenantId, source.AppId, ct);
                    await Store($"/snapshot/{pages++}", encrypted);
                }
                }
                snapshot = new Snapshot(pages, wrappedKey, started, configurationHash);
                await Store("/snapshot", JsonSerializer.Serialize(snapshot));
                await uow.CommitAsync(ct);
            }
            catch { await uow.RollbackAsync(CancellationToken.None); throw; }
        }
        else snapshot = JsonSerializer.Deserialize<Snapshot>(manifestJson)!;
        if (snapshot.ConfigurationHash != configurationHash)
            throw new PipelineNonRetryableException("Copy Records configuration changed during this execution. Start a new run instead of replaying the saved snapshot.");
        long inserted = 0, updated = 0, errors = 0;
        var messages = new List<string>();
        var failedRecords = new List<CopyFailedRecord>();
        var failedRecordsTruncated = false;
        // Names a rejected source record in the run history: its Record ID#, the merge value it was matched on, and the
        // values it carried — enough to find it in the source table — together with why it was rejected.
        CopyFailedRecord DescribeFailure(Dictionary<string, JsonElement> row, string reason)
        {
            long? sourceId = row.TryGetValue("Id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id) ? id : null;
            string? mergeValue = null;
            try { mergeValue = KeyOf(BuildValues(row)); } catch (ValidationException) { }
            var carried = new Dictionary<string, string?>();
            foreach (var field in exported)
            {
                if (!row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var element)) continue;
                carried[CopyRecordsDefinition.DisplayName(field)] = element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => element.GetString(),
                    _ => element.ToString()
                };
            }
            return new CopyFailedRecord(row["PublicId"].GetString() ?? string.Empty, sourceId, mergeValue, reason, carried);
        }
        // The merge field stores ciphertext when encrypted, so an "=" SQL lookup per row can
        // never match. Build a decrypted key -> record index once up front instead (same
        // client-side approach RecordRepository.GetDistinctFieldValuesAsync uses for encrypted
        // fields), and keep it updated as rows are inserted so later rows in this same run still
        // see them, matching what a live per-row SQL lookup would have seen.
        var mergeIndex = merge.IsEncrypted
            ? await BuildEncryptedMergeIndexAsync(records, destination, destinationFields, merge, ct)
            : null;

        // Performance: where each part of the copy spends its time, logged once at the end (see the log line below).
        var phaseTicks = new Dictionary<string, long>();
        var phaseCalls = new Dictionary<string, int>();
        void AddPhase(string phase, long startedAt)
        {
            phaseTicks[phase] = phaseTicks.GetValueOrDefault(phase) + (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            phaseCalls[phase] = phaseCalls.GetValueOrDefault(phase) + 1;
        }
        var copyClock = System.Diagnostics.Stopwatch.StartNew();
        var fastPages = 0;
        var slowPages = 0;
        var fallbackPages = 0;

        // A plain (not encrypted) Text or Number merge field is looked up for a whole page of rows with one IN query, then kept in
        // memory: the same answer a per-row "=" query gives, without a query per row. Anything else keeps the per-row query.
        var mergeBatchable = !merge.IsEncrypted && merge.Fid.HasValue && merge.TypeCode is "Text" or "Number";
        var mergeNumeric = merge.TypeCode == "Number";
        var mergeLive = mergeBatchable ? new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase) : null;
        var mergeLooked = mergeBatchable ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        var mergeDictionaryUnsafe = false;
        // Key as the database compares it: numbers by value (5 = 5.0000), text ignoring case and trailing spaces. Null = cannot be
        // looked up in memory (not a number), so that row uses the per-row query.
        string? MergeKey(object? value)
        {
            if (value is null) return null;
            if (mergeNumeric)
            {
                decimal number;
                if (value is decimal or double or float or int or long or short or byte) number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                else if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
                return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd();
        }
        async Task PrefetchMergeAsync(IEnumerable<string?> keys)
        {
            if (!mergeBatchable) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var lookup = new List<object>();
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key) || MergeKey(key) is not { } normalized || !mergeLooked!.Add(normalized)) continue;
                if (!mergeNumeric) { lookup.Add(key); continue; }
                var number = decimal.Parse(key, NumberStyles.Float, CultureInfo.InvariantCulture);
                // The Record ID# column is a bigint: compare it with a bigint (a decimal would make SQL Server convert the whole column,
                // which cannot use the index). A fractional number can never equal a whole Record ID#.
                if (!merge.IsSystem) lookup.Add(number);
                else if (number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue) lookup.Add((long)number);
            }
            foreach (var (value, id) in lookup.Count == 0 ? Array.Empty<(object? Value, Guid PublicId)>() : await records.FindRecordsByFieldValuesAsync(destination, merge, lookup, uow.Transaction, ct))
            {
                if (MergeKey(value) is not { } hit) continue;
                // A value the database called equal but the in-memory comparison does not: stop trusting the dictionary.
                if (!mergeLooked!.Contains(hit)) mergeDictionaryUnsafe = true;
                if (!mergeLive!.TryGetValue(hit, out var list)) mergeLive[hit] = list = new List<Guid>();
                list.Add(id);
            }
            AddPhase("merge-lookup", started);
        }
        // What a live lookup of the destination record with this merge value would have found: encrypted fields use the
        // in-memory index, plain Text/Number fields the prefetched page, anything else one query.
        async Task<IReadOnlyDictionary<string, object?>?> FindExistingAsync(string? keyStr)
        {
            if (string.IsNullOrEmpty(keyStr)) return null;
            if (merge.IsEncrypted)
            {
                if (!mergeIndex!.TryGetValue(EncryptedMergeKey(merge, keyStr), out var matches)) return null;
                if (matches.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
                return matches[0];
            }
            if (mergeBatchable && !mergeDictionaryUnsafe && MergeKey(keyStr) is { } normalized && mergeLooked!.Contains(normalized))
            {
                if (!mergeLive!.TryGetValue(normalized, out var ids) || ids.Count == 0) return null;
                if (ids.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
                return new Dictionary<string, object?> { ["PublicId"] = ids[0] };
            }
            var match = new FilterGroup
            {
                Nodes = new() { new() { Condition = new()
            { FieldId = merge.Fid!.Value, Operator = "eq", Value = keyStr } } }
            };
            var found = await records.ListAsync(destination, destinationFields, page: 1, pageSize: 2, filterTree: match, ct: ct);
            if (found.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
            return found.FirstOrDefault();
        }
        // Later rows of this run must see a record this run created.
        void RememberCreated(string? keyStr, Guid publicId)
        {
            if (string.IsNullOrEmpty(keyStr)) return;
            if (merge.IsEncrypted)
            {
                var encryptedKey = EncryptedMergeKey(merge, keyStr);
                if (!mergeIndex!.TryGetValue(encryptedKey, out var list))
                    mergeIndex[encryptedKey] = list = new List<IReadOnlyDictionary<string, object?>>();
                list.Add(new Dictionary<string, object?> { ["PublicId"] = publicId });
            }
            else if (mergeBatchable && MergeKey(keyStr) is { } normalized && mergeLooked!.Contains(normalized))
            {
                if (!mergeLive!.TryGetValue(normalized, out var ids)) mergeLive[normalized] = ids = new List<Guid>();
                ids.Add(publicId);
            }
        }
        Dictionary<long, object?> BuildValues(Dictionary<string, JsonElement> row)
        {
            var values = new Dictionary<long, object?>();
            for (int i = 0; i < exported.Count; i++)
            {
                var field = exported[i];
                row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var value);
                if (value.ValueKind == JsonValueKind.Undefined)
                    throw CopyRecordsDefinition.Error($"Source field '{CopyRecordsDefinition.DisplayName(field)}' has no exported value.");
                object? raw = value;
                if (PhysicalNaming.IsRangeTypeCode(field.TypeCode))
                {
                    row.TryGetValue(PhysicalNaming.EndColumnName(field.Fid!.Value), out var end);
                    raw = JsonSerializer.Serialize(new { start = value, end = end.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : end });
                }
                values[imported[i].Fid!.Value] = CopyRecordsDefinition.ConvertValue(raw, field, imported[i]);
            }
            return values;
        }
        var uniqueFields = destinationFields.Where(f => f.Fid.HasValue && !f.IsSystem && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && f.IsUnique && f.Fid != merge.Fid).ToList();
        var memoryUnique = new Dictionary<long, HashSet<string>?>();   // unique fid -> values held, for fields whose values are stored encrypted
        // A unique value as the database compares it: numbers by value (5 = 5.0000), text ignoring case and trailing spaces.
        // An encrypted field is compared the way HasValueDuplicateAsync compares it: both sides trimmed, ignoring case.
        string UniqueKey(AppField field, object value)
        {
            if (memoryUnique.ContainsKey(field.Fid!.Value)) return (value.ToString() ?? string.Empty).Trim();
            if (field.TypeCode == "Number" && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd() ?? string.Empty;
        }
        // A unique field whose values are stored encrypted cannot be compared in SQL, and checking one new record against it means
        // decrypting the whole column — so a copy got slower with every record the table held (n records x n decryptions, and the
        // database busy for every other run meanwhile). Its values are read and decrypted once, and every record is checked against
        // that set in memory, which new records join as they are written. A null entry = to be (re)read before the next page.
        async Task EnsureMemoryUniqueAsync()
        {
            foreach (var fid in memoryUnique.Where(p => p.Value == null).Select(p => p.Key).ToList())
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var field = uniqueFields.First(f => f.Fid == fid);
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in await records.GetAllValuesForUniqueCheckAsync(destination, field, null, ct)) keys.Add(value.Trim());
                memoryUnique[fid] = keys;
                AddPhase("unique-index", started);
            }
        }
        // The row-by-row path's answer for a unique value: from the in-memory index when the field has one, otherwise "ask the database".
        bool? KnownDuplicate(AppField field, object value)
            => memoryUnique.TryGetValue(field.Fid!.Value, out var held) && held != null ? held.Contains(UniqueKey(field, value)) : null;
        void RememberUnique(Dictionary<long, object?> values)
        {
            foreach (var (fid, held) in memoryUnique)
                if (held != null && values.TryGetValue(fid, out var value) && value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                    held.Add(UniqueKey(uniqueFields.First(f => f.Fid == fid), value));
        }
        string? KeyOf(Dictionary<long, object?> values)
        {
            values.TryGetValue(merge.Fid!.Value, out var key);
            return key != null ? Convert.ToString(key, CultureInfo.InvariantCulture) : null;
        }
        async Task ApplyUpdateAsync(IReadOnlyDictionary<string, object?> existing, Dictionary<long, object?> values)
        {
            var identity = existing.FirstOrDefault(p => p.Key.Equals("PublicId", StringComparison.OrdinalIgnoreCase)).Value;
            if (!Guid.TryParse(identity?.ToString(), out var publicId)) throw new InvalidOperationException("Destination record identity is missing.");
            if (!destinationAccess.Unrestricted)
            {
                // GetRecord enforces both physical and computed role filters.
                await services.GetRequiredService<PowerBase.Application.Records.Queries.GetRecord.GetRecordQueryHandler>()
                    .HandleAsync(new PowerBase.Application.Records.Queries.GetRecord.GetRecordQuery(destination.PublicId, publicId), ct);
                if (!await records.ExistsWithViewFilterAsync(destination, destinationFields, publicId, destinationAccess.ViewFilter, destinationAccess.RestrictToCreatedBy, ct))
                    throw new UnauthorizedActionException("The matched destination record is not accessible.");
                if (destinationAccess.ModifyScope == RecordScopes.OwnRecords) await enforcer.EnsureRecordOwnedAsync(destination, publicId, ct);
            }
            await writes.ApplyAsync(destination, destinationFields, publicId, values, AuditActions.Updated,
                "Record updated via PowerFlow Copy Records", ct, uow.Transaction);
            // An update can change a unique value (and free the old one): the in-memory index is read again before it is trusted.
            foreach (var fid in memoryUnique.Keys.ToList())
                if (values.ContainsKey(fid)) memoryUnique[fid] = null;
        }
        // Everything a new record goes through before it is written (defaults, reference / user / constraint / data-rule checks).
        // Returns why the record is rejected (Required / Unique / format), or null when it can be written. A rejection is a RESULT here, not an
        // exception: throwing one per rejected record cost ~50 ms each under a debugger, so a copy that rejects thousands of records took minutes.
        async Task<string?> PrepareInsertAsync(Dictionary<long, object?> values, Func<AppField, object, bool?>? isDuplicate = null)
        {
            foreach (var field in destinationFields.Where(f => !f.IsSystem && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            {
                if (!values.ContainsKey(field.Fid!.Value) && !string.IsNullOrWhiteSpace(field.DefaultValue))
                    values[field.Fid.Value] = await DefaultValue(field, queryContext, ct);
            }
            await PipelineReferenceTargets.EnsureAvailableAsync(destinationFields, values, tableRepo, ct);
            var overrides = await ReferenceWriteValidator.ValidateAsync(destinationFields, values, tableRepo, fieldRepo, records, relRepo, ct);
            foreach (var pair in overrides) values[pair.Key] = pair.Value;
            await UserFieldValueResolver.ResolveAsync(services.GetRequiredService<IUserRepository>(), destinationFields, values, ct);
            var violations = await RecordConstraintValidator.CollectViolationsAsync(destination, destinationFields, values, records, true, null, ct, transaction: uow.Transaction, isDuplicate: isDuplicate);
            if (violations.Count > 0)
                // Built (not thrown) so the message reads exactly as the validator's own exception would.
                return new ValidationException(violations.GroupBy(v => v.FieldId.ToString())
                    .ToDictionary(g => g.Key, g => g.Select(v => v.Message).ToArray())).Message;
            await CustomDataRuleValidator.ValidateAsync(destination, destinationFields, values, tableRepo, fieldRepo, records, services.GetRequiredService<FormulaEngine>(), ct);
            return null;
        }
        var identityField = destinationFields.SingleOrDefault(f => f.IsSystem && f.PhysicalColumnName == "Id" && f.Fid.HasValue);
        var copyCreatedOnField = destinationFields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedOn" && f.Fid.HasValue);
        var copyCreatedByField = destinationFields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
        var messagePublisher = services.GetService<IMessagePublisher>();
        // Whether any pipeline listens to record events on the destination table, asked once per page: when nobody does, every record's
        // trigger call would only look the listeners up again (one control-database round trip per record) and find none.
        var triggerInterceptor = services.GetRequiredService<IPipelineTriggerInterceptor>();
        bool? pageHasListeners = null;
        async Task<bool> HasListenersAsync()
        {
            if (pageHasListeners is { } known) return known;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            pageHasListeners = await triggerInterceptor.HasActiveSubscriptionsAsync(destination, ct);
            AddPhase("trigger-check", started);
            return pageHasListeners.Value;
        }

        // Whole pages of new records are written together when nothing in the destination can make one row depend on another row
        // written in the same page: no unique field other than the merge field (checked against the database row by row; the merge field's own duplicates are caught below), no reference field (it may point
        // at this very table) and no custom data rule (it may read the table). Everything else keeps the row-by-row path.
        // A plain Text/Number unique field (other than the merge field) no longer rules out the batch path: the page's values are checked
        // against the table with one query per field and against each other in memory, in the same order rows would be checked one by one.
        // An app that encrypts a field without flagging the field itself is found out the first time the page query below cannot compare it.
        foreach (var field in uniqueFields)
            if (field.IsEncrypted) memoryUnique[field.Fid!.Value] = null;
        // A Reference field only ties one row to another written in the same page when it can point at the destination table itself. One that
        // points at a different table (like "Related A1" on A2) cannot, so it no longer sends the whole copy down the row-by-row path — which was
        // ~150 ms per record even when the field was not part of the copy at all.
        bool MayReferenceDestination(AppField reference)
            => FormulaTypeMap.ParseReferenceSettings(reference.Settings)?.ParentTableId is not { } parentTableId || parentTableId == destination.Id;
        var fastEligible = !(destination.IsCustomDataRuleEnabled && !string.IsNullOrWhiteSpace(destination.CustomDataRule))
            && uniqueFields.All(f => f.TypeCode is "Text" or "Number")
            && !destinationFields.Any(f => f.Fid.HasValue && !f.IsSystem && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && f.TypeCode == "Reference" && MayReferenceDestination(f))
            && (merge.IsEncrypted || (mergeBatchable && (!merge.IsSystem || merge.PhysicalColumnName == "Id")));

        static string PageReceiptSuffix(int pageIndex) => $"/page/{pageIndex}";

        async Task<Dictionary<string, string>> ReadManyAsync(IReadOnlyList<string> suffixes)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var hashes = suffixes.Select(s => Hash(prefix + s)).ToList();
            var found = await receipts.GetManyByExecutionKeysAsync(messageId, stepId, hashes, uow.Transaction, ct);
            var map = new Dictionary<string, string>(suffixes.Count);
            for (var i = 0; i < suffixes.Count; i++)
                if (found.TryGetValue(Convert.ToHexString(hashes[i]), out var json)) map[suffixes[i]] = json;
            AddPhase("receipts-read", started);
            return map;
        }
        PipelineStepIdempotencyLog ReceiptLog(string suffix, Receipt receipt) => new()
        {
            MessageId = messageId,
            StepPublicId = stepId,
            ExecutionPath = prefix + suffix,
            ExecutionPathHash = Hash(prefix + suffix),
            OutputJson = JsonSerializer.Serialize(receipt)
        };

        // Writes one page of rows in a single transaction. Returns each row's receipt, or null when the page has to be done
        // row by row instead (nothing from the attempt is kept in that case).
        async Task<Dictionary<string, Receipt>?> TryFastPageAsync(List<(string RowKey, Dictionary<string, JsonElement> Row)> pending, int pageIndex)
        {
            var results = new Dictionary<string, Receipt>(pending.Count);
            var newRows = new List<(string RowKey, Dictionary<long, object?> Values, string? KeyStr)>();
            var pageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexMessages = new List<PowerBase.Application.Common.Models.SearchIndexMessage>();
            await uow.BeginAsync(ct);
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                // Unique fields: which of this page's values the table already holds (one query per field), and which values
                // earlier rows of this very page are about to take. Together they answer "is this value in use?" exactly as the
                // per-row lookup would have, row by row in page order.
                var uniqueChecked = new Dictionary<long, HashSet<string>>();
                var uniqueInDb = new Dictionary<long, HashSet<string>>();
                var uniqueInPage = new Dictionary<long, HashSet<string>>();
                foreach (var uniqueField in uniqueFields)
                {
                    var fid = (long)uniqueField.Fid!.Value;
                    uniqueInPage[fid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // Encrypted values are checked against the run's in-memory index instead (see EnsureMemoryUniqueAsync).
                    if (memoryUnique.ContainsKey(fid)) continue;
                    var candidates = new List<object>();
                    var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, row) in pending)
                    {
                        try
                        {
                            if (BuildValues(row).TryGetValue(fid, out var candidate) && candidate != null
                                && !PhysicalNaming.IsRequiredMissing(uniqueField.TypeCode, candidate)
                                && candidateKeys.Add(UniqueKey(uniqueField, candidate)))
                                candidates.Add(candidate);
                        }
                        catch (ValidationException) { }   // rejected on its own below
                    }
                    IReadOnlySet<int> taken = new HashSet<int>();
                    if (candidates.Count > 0)
                    {
                        try { taken = await records.GetExistingValueIndexesAsync(destination, uniqueField, candidates, uow.Transaction, ct); }
                        catch (NotSupportedException)
                        {
                            // This app encrypts the field's values, so SQL cannot compare them: from here on it is checked against the in-memory index
                            // (read before the next page); this page is redone row by row.
                            memoryUnique[fid] = null;
                            throw new FastPageNotApplicableException();
                        }
                    }
                    uniqueChecked[fid] = candidateKeys;
                    uniqueInDb[fid] = new HashSet<string>(taken.Select(i => UniqueKey(uniqueField, candidates[i])), StringComparer.OrdinalIgnoreCase);
                }
                bool? IsUniqueValueTaken(AppField field, object value)
                {
                    var fid = (long)field.Fid!.Value;
                    // The merge field is unique too but is not one of this page's batch-checked fields: the database answers for it, as before.
                    if (!uniqueInPage.ContainsKey(fid)) return null;
                    var key = UniqueKey(field, value);
                    if (memoryUnique.ContainsKey(fid))
                    {
                        // An earlier row of this page updated a record's unique value, so the index is being rebuilt: redo the page row by row.
                        if (memoryUnique[fid] is not { } held) throw new FastPageNotApplicableException();
                        return held.Contains(key) || uniqueInPage[fid].Contains(key);
                    }
                    // A value no row of this page offered (e.g. one a default just filled in) was never looked up: do the page row by row.
                    if (!uniqueChecked.TryGetValue(fid, out var asked) || !asked.Contains(key)) throw new FastPageNotApplicableException();
                    return uniqueInDb[fid].Contains(key) || uniqueInPage[fid].Contains(key);
                }
                foreach (var (rowKey, row) in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    Dictionary<long, object?> values;
                    string? keyStr;
                    IReadOnlyDictionary<string, object?>? existing;
                    try
                    {
                        values = BuildValues(row);
                        keyStr = KeyOf(values);
                        existing = await FindExistingAsync(keyStr);
                        foreach (var systemField in destinationFields.Where(f => f.IsSystem && f.Fid.HasValue))
                            values.Remove(systemField.Fid!.Value); // System identities are match-only, never writable.
                        var rejection = existing == null ? await PrepareInsertAsync(values, uniqueFields.Count > 0 ? IsUniqueValueTaken : null) : null;
                        if (rejection != null)
                        {
                            // Rejected before anything was written for this row, so only this row is rejected.
                            results[rowKey] = new(false, false, rejection);
                            continue;
                        }
                    }
                    catch (ValidationException ex)
                    {
                        // Raised before anything was written for this row, so only this row is rejected.
                        results[rowKey] = new(false, false, ex.Message);
                        continue;
                    }
                    if (existing != null)
                    {
                        var updateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        await ApplyUpdateAsync(existing, values);
                        AddPhase("update", updateStarted);
                        results[rowKey] = new(false, true);
                        continue;
                    }
                    // Two new rows with the same merge value: the second one must update the first, which only the row-by-row path does.
                    if (!string.IsNullOrEmpty(keyStr) && !pageKeys.Add(merge.IsEncrypted ? EncryptedMergeKey(merge, keyStr) : MergeKey(keyStr) ?? keyStr))
                        throw new FastPageNotApplicableException();
                    newRows.Add((rowKey, values, keyStr));
                    // This row is going in: later rows of the page must now see its unique values as taken.
                    foreach (var uniqueField in uniqueFields)
                        if (values.TryGetValue(uniqueField.Fid!.Value, out var taken) && taken != null && !PhysicalNaming.IsRequiredMissing(uniqueField.TypeCode, taken))
                            uniqueInPage[uniqueField.Fid.Value].Add(UniqueKey(uniqueField, taken));
                }
                AddPhase("validate", started);

                var created = new List<Guid>();
                if (newRows.Count > 0)
                {
                    started = System.Diagnostics.Stopwatch.GetTimestamp();
                    created = (await records.CreateManyAsync(destination, destinationFields,
                        newRows.Select(n => (IReadOnlyDictionary<long, object?>)n.Values).ToList(), uow.Transaction!, ct,
                        messagePublisher != null ? indexMessages.Add : null)).ToList();
                    if (created.Count != newRows.Count) throw new InvalidOperationException("Copy Records: the batch insert returned an unexpected number of records.");
                    AddPhase("insert", started);

                    if (identityField != null)
                    {
                        started = System.Diagnostics.Stopwatch.GetTimestamp();
                        var recordIds = new Dictionary<Guid, long>(created.Count);
                        foreach (var chunk in created.Chunk(sqlIdChunkSize / 2))
                            foreach (var pair in await records.GetActiveRecordIdsByPublicIdsAsync(destination, chunk, uow.Transaction, ct))
                                recordIds[pair.Key] = pair.Value;
                        for (var i = 0; i < newRows.Count; i++)
                            newRows[i].Values[identityField.Fid!.Value] = recordIds.TryGetValue(created[i], out var recordId)
                                ? recordId
                                : throw new InvalidOperationException("Destination record identity is missing.");
                        AddPhase("record-ids", started);
                        // A later row whose merge value is the Record ID# an earlier row of this page just received would, row by row,
                        // have updated that new record instead of creating another.
                        if (merge.IsSystem)
                        {
                            var createdAt = new Dictionary<string, int>(newRows.Count);
                            for (var i = 0; i < newRows.Count; i++)
                                if (MergeKey(newRows[i].Values[identityField.Fid!.Value]) is { } idKey) createdAt[idKey] = i;
                            for (var j = 0; j < newRows.Count; j++)
                                if (MergeKey(newRows[j].KeyStr) is { } wanted && createdAt.TryGetValue(wanted, out var i) && i < j)
                                    throw new FastPageNotApplicableException();
                        }
                    }
                    // Date Created / Record Owner were stripped from values above (system identities are match-only, never writable),
                    // so a pipeline trigger firing off this copy-created record could never resolve {{steps.<trigger>.fid_N}} for them.
                    var createdOn = DateTime.UtcNow;
                    foreach (var row in newRows)
                    {
                        if (copyCreatedOnField != null) row.Values[copyCreatedOnField.Fid!.Value] = createdOn;
                        if (copyCreatedByField != null) row.Values[copyCreatedByField.Fid!.Value] = queryContext.UserId;
                    }
                    started = System.Diagnostics.Stopwatch.GetTimestamp();
                    await services.GetRequiredService<IAuditRepository>().LogActivitiesAsync(AuditActions.Created, AuditEntityTypes.Record,
                        created.Select(id => id.ToString()).ToList(), "Record created via PowerFlow Copy Records", destination.AppId, uow.Transaction, ct);
                    await tableRepo.IncrementRecordCountByAsync(destination.Id, created.Count, uow.Transaction, ct);
                    AddPhase("audit-count", started);

                    if (await HasListenersAsync())
                    {
                        started = System.Diagnostics.Stopwatch.GetTimestamp();
                        await triggerInterceptor.InterceptAddedManyAsync(destination, destinationFields,
                            newRows.Select((n, i) => (created[i], (IReadOnlyDictionary<long, object?>)n.Values)).ToList(), ct);
                        AddPhase("triggers", started);
                    }
                    for (var i = 0; i < newRows.Count; i++) results[newRows[i].RowKey] = new(true, false);
                }

                started = System.Diagnostics.Stopwatch.GetTimestamp();
                // One receipt for the whole page (every row's result), written in the same transaction as the rows: a replay reads one key instead of
                // one per row, and writing one row instead of 250 was the largest single cost of a copy.
                await receipts.InsertAsync(new PipelineStepIdempotencyLog
                {
                    MessageId = messageId,
                    StepPublicId = stepId,
                    ExecutionPath = prefix + PageReceiptSuffix(pageIndex),
                    ExecutionPathHash = Hash(prefix + PageReceiptSuffix(pageIndex)),
                    OutputJson = JsonSerializer.Serialize(results)
                }, uow.Transaction, ct);
                AddPhase("receipts-write", started);
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                await uow.CommitAsync(ct);
                AddPhase("commit", started);
                // The page is committed: its unique values now belong to the run's in-memory index.
                foreach (var (fid, held) in memoryUnique)
                    if (held != null && uniqueInPage.TryGetValue(fid, out var added)) held.UnionWith(added);

                for (var i = 0; i < newRows.Count; i++)
                    RememberCreated(merge.IsSystem && identityField != null ? Convert.ToString(newRows[i].Values[identityField.Fid!.Value], CultureInfo.InvariantCulture) : newRows[i].KeyStr, created[i]);
                if (messagePublisher != null && indexMessages.Count > 0)
                {
                    // Queued in the background, like the one-by-one insert always did: the records are committed, the search index catches up.
                    var count = indexMessages.Count;
                    _ = messagePublisher.PublishBatchAsync(indexMessages, CancellationToken.None).ContinueWith(
                        t => copyLogger?.LogWarning("[CopyRecords] Could not queue the search-index updates for {Count} copied records: {Error}", count, t.Exception!.GetBaseException().Message),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                return results;
            }
            catch (Exception ex)
            {
                await uow.RollbackAsync(CancellationToken.None);
                if (ex is OperationCanceledException) throw;
                if (ex is not FastPageNotApplicableException)
                    copyLogger?.LogWarning(ex, "[CopyRecords] Batch write of {Count} rows failed; writing the page row by row.", pending.Count);
                return null;
            }
        }

        for (var pageIndex = 0; pageIndex < snapshot.Pages; pageIndex++)
        {
            var encrypted = await Read($"/snapshot/{pageIndex}") ?? throw new InvalidOperationException("Copy Records snapshot page is missing.");
            var json = await encryption.DecryptDataAsync(encrypted, snapshot.WrappedKey, queryContext.TenantId, source.AppId, ct);
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
            pageHasListeners = null;
            await EnsureMemoryUniqueAsync();
            // A page written in one go has a single receipt for all its rows; any other row has its own receipt.
            var cachedReceipts = new Dictionary<string, string>();
            var pageReceiptJson = await Read(PageReceiptSuffix(pageIndex));
            if (pageReceiptJson != null)
                foreach (var (rowKey, pageReceipt) in JsonSerializer.Deserialize<Dictionary<string, Receipt>>(pageReceiptJson)!)
                    cachedReceipts[rowKey] = JsonSerializer.Serialize(pageReceipt);
            var uncovered = rows.Select(r => "/row/" + r["PublicId"].GetString()).Where(k => !cachedReceipts.ContainsKey(k)).ToList();
            foreach (var (rowKey, rowReceipt) in uncovered.Count == 0 ? new Dictionary<string, string>() : await ReadManyAsync(uncovered))
                cachedReceipts[rowKey] = rowReceipt;
            var pendingRows = rows.Select(r => (RowKey: "/row/" + r["PublicId"].GetString(), Row: r)).Where(p => !cachedReceipts.ContainsKey(p.RowKey)).ToList();
            Dictionary<string, Receipt>? pageResults = null;
            if (pendingRows.Count > 0)
            {
                if (mergeBatchable)
                {
                    // Keys of rows that cannot be converted fail on their own below; they only skip the prefetch.
                    var keys = new List<string?>(pendingRows.Count);
                    foreach (var (_, row) in pendingRows)
                    {
                        try { keys.Add(KeyOf(BuildValues(row))); }
                        catch (ValidationException) { }
                    }
                    await PrefetchMergeAsync(keys);
                }
                if (fastEligible)
                {
                    pageResults = await TryFastPageAsync(pendingRows, pageIndex);
                    if (pageResults != null) fastPages++; else fallbackPages++;
                }
                else slowPages++;
            }
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var rowKey = "/row/" + row["PublicId"].GetString();
                Receipt receipt;
                if (cachedReceipts.TryGetValue(rowKey, out var cached)) receipt = JsonSerializer.Deserialize<Receipt>(cached)!;
                else if (pageResults != null && pageResults.TryGetValue(rowKey, out var fastReceipt)) receipt = fastReceipt;
                else
                {
                    var rowStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    await uow.BeginAsync(ct);
                    try
                    {
                        var values = BuildValues(row);
                        var keyStr = KeyOf(values);
                        var existing = await FindExistingAsync(keyStr);
                        foreach (var systemField in destinationFields.Where(f => f.IsSystem && f.Fid.HasValue))
                            values.Remove(systemField.Fid!.Value); // System identities are match-only, never writable.
                        if (existing != null)
                        {
                            await ApplyUpdateAsync(existing, values);
                            receipt = new(false, true);
                        }
                        else if (await PrepareInsertAsync(values, KnownDuplicate) is { } rejection)
                        {
                            // Nothing was written for this row: the rejection is its receipt, committed with the row's own transaction.
                            receipt = new(false, false, rejection);
                        }
                        else
                        {
                            var publicId = await records.CreateAsync(destination, destinationFields, values, uow.Transaction, ct);
                            if (identityField != null)
                                values[identityField.Fid!.Value] = await records.GetActiveRecordIdByPublicIdAsync(destination, publicId, uow.Transaction, ct);
                            // Date Created / Record Owner were stripped from values above (system
                            // identities are match-only, never writable), so a pipeline trigger
                            // firing off this copy-created record could never resolve
                            // {{steps.<trigger>.fid_N}} for them. Same fix as
                            // CreateRecordCommandHandler's analogous backfill.
                            if (copyCreatedOnField != null) values[copyCreatedOnField.Fid!.Value] = DateTime.UtcNow;
                            if (copyCreatedByField != null) values[copyCreatedByField.Fid!.Value] = queryContext.UserId;
                            await services.GetRequiredService<IAuditRepository>().LogActivityAsync(AuditActions.Created, AuditEntityTypes.Record,
                                publicId.ToString(), "Record created via PowerFlow Copy Records", appId: destination.AppId, ct: ct);
                            await tableRepo.IncrementRecordCountAsync(destination.Id, ct);
                            if (await HasListenersAsync())
                                await triggerInterceptor.InterceptAsync(destination, destinationFields, publicId, values, "record-added", ct);
                            RememberCreated(merge.IsSystem && identityField != null ? Convert.ToString(values[identityField.Fid!.Value], CultureInfo.InvariantCulture) : keyStr, publicId);
                            receipt = new(true, false);
                        }
                        await Store(rowKey, JsonSerializer.Serialize(receipt));
                        await uow.CommitAsync(ct);
                        if (receipt.Inserted) RememberUnique(values);
                    }
                    catch (ValidationException ex)
                    {
                        await uow.RollbackAsync(CancellationToken.None);
                        receipt = new(false, false, ex.Message);
                        await uow.BeginAsync(ct);
                        try { await Store(rowKey, JsonSerializer.Serialize(receipt)); await uow.CommitAsync(ct); }
                        catch { await uow.RollbackAsync(CancellationToken.None); throw; }
                    }
                    catch { await uow.RollbackAsync(CancellationToken.None); throw; }
                    AddPhase("row-by-row", rowStarted);
                }
                if (receipt.Inserted) inserted++;
                if (receipt.Updated) updated++;
                if (receipt.Error != null)
                {
                    errors++;
                    if (messages.Count < 20) messages.Add(receipt.Error);
                    if (failedRecords.Count < CopyFailedRecordStore.MaxStored) failedRecords.Add(DescribeFailure(row, receipt.Error));
                    else failedRecordsTruncated = true;
                }
            }
        }
        copyLogger?.LogInformation(
            "[CopyRecords] Wrote {Inserted} inserted, {Updated} updated, {Errors} rejected rows in {ElapsedMs} ms ({FastPages} pages in batch, {FallbackPages} batch pages redone row by row, {SlowPages} pages row by row). Time by part (ms/calls): {Phases}",
            inserted, updated, errors, copyClock.ElapsedMilliseconds, fastPages, fallbackPages, slowPages,
            string.Join(", ", phaseTicks.OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key}={p.Value * 1000 / System.Diagnostics.Stopwatch.Frequency}/{phaseCalls[p.Key]}")));
        // Every rejected record is kept (in pages, in the step's receipts) for the run history to page through; the step output
        // itself carries only a short preview, so a copy that rejects thousands of records does not produce a huge output.
        var failedPages = failedRecords.Chunk(CopyFailedRecordStore.PageSize).ToList();
        var output = JsonSerializer.Serialize(new Summary(inserted, updated, errors, messages,
            failedRecords.Take(CopyFailedRecordStore.PreviewCount).ToList(), failedRecordsTruncated, failedPages.Count, failedRecords.Count));
        await uow.BeginAsync(ct);
        try
        {
            for (var i = 0; i < failedPages.Count; i++) await Store(CopyFailedRecordStore.PageSuffix(i), JsonSerializer.Serialize(failedPages[i]));
            await Store("/complete", output);
            await uow.CommitAsync(ct);
        }
        catch { await uow.RollbackAsync(CancellationToken.None); throw; }
        return Finish(output);
    }

    /// <summary>Scans the destination table (paginated, decrypted) once and indexes it by the
    /// encrypted merge field's plaintext value, case-insensitively, so per-row merge matching
    /// during Copy Records doesn't run an "=" SQL comparison against ciphertext (which can never
    /// match) and doesn't re-scan the whole table per row.</summary>
    /// <summary>The key an encrypted merge value is matched by. An encrypted field holds its value as text, so a Number written as
    /// "8000" by one path and "8000.0000" by another would be two different keys — a copy then created a second record instead of
    /// updating the first. Numbers are keyed by value, as SQL compares an unencrypted merge field; any other text as it is.</summary>
    public static string EncryptedMergeKey(AppField merge, string key)
    {
        if (PipelineFilterEvaluator.GetTypeCategory(merge) == "NUMBER" &&
            decimal.TryParse(key.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
        return key;
    }

    private static async Task<Dictionary<string, List<IReadOnlyDictionary<string, object?>>>> BuildEncryptedMergeIndexAsync(
        IRecordRepository records, AppTable destination, IReadOnlyList<AppField> destinationFields, AppField merge, CancellationToken ct)
    {
        var col = PhysicalNaming.GetPhysicalColumnName(merge);
        var index = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        const int pageSize = 5000;
        for (var page = 1; ; page++)
        {
            var rows = await records.ListAsync(destination, destinationFields, page, pageSize, filterTree: null, ct: ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(col, out var val) || val == null) continue;
                var key = Convert.ToString(val, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key)) continue;
                key = EncryptedMergeKey(merge, key);
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<IReadOnlyDictionary<string, object?>>();
                list.Add(row);
            }
            if (rows.Count < pageSize) break;
        }
        return index;
    }
}
