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
    private sealed record Summary(long InsertedCount, long UpdatedCount, long ErrorCount, List<string> Errors);
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

    public async Task<string> ExecuteAsync(CopyRecordsDefinition config, string query, Guid stepId,
        Guid messageId, string executionPath, CancellationToken cancellationToken)
    {
        config.ValidateShape();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(1));
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
        await accessService.RequirePermissionByTablePublicIdAsync(sourceId, PermissionCodes.RecordsRead, ct);
        await accessService.RequirePermissionByTablePublicIdAsync(destinationId, PermissionCodes.RecordsCreate, ct);
        await accessService.RequirePermissionByTablePublicIdAsync(destinationId, PermissionCodes.RecordsUpdate, ct);
        var source = await tableRepo.GetByPublicIdAsync(sourceId, ct);
        var destination = await tableRepo.GetByPublicIdAsync(destinationId, ct);
        var sourceFields = await fieldRepo.ListByTableAsync(source.Id, ct);
        var destinationFields = await fieldRepo.ListByTableAsync(destination.Id, ct);
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

        // Performance: when Azure AI Search grid-search is enabled (same UseAzureAiForGridSearch
        // flag Reports and pipeline Search Records use), resolve the source-side filter through
        // the search index instead of scanning the source table. Read-only and pre-transaction —
        // this only narrows which rows the snapshot read below selects, it never guards a write.
        // The index stores decrypted plaintext for encrypted fields, so this also naturally
        // handles encrypted-field conditions in `query` without the in-memory split further down.
        // Falls back to the SQL path (which has its own encrypted-field handling) on any failure.
        // Result: a list of Id-only filters, one per SQL parameter chunk (usually just one).
        List<FilterGroup?>? aiSearchIdFilterChunks = null;
        var azureSearch = services.GetService<IAzureSearchService>();
        if (azureSearch != null && azureSearch.IsGridSearchEnabled && effectiveFilter.Nodes.Count > 0
            && sourceFields.Any(f => f.IsSearchable || f.IsFilterable) && await azureSearch.IsHealthyAsync(ct))
        {
            var odata = PowerBase.Application.Reports.Queries.RunReport.OData.ODataFilterBuilder.Build(effectiveFilter, sourceFields);
            if (!string.IsNullOrWhiteSpace(odata))
            {
                try
                {
                    var aiMatches = await azureSearch.SearchRecordsByFilterAsync(queryContext.TenantId, source.Id, odata, ct);
                    var matchedIds = new List<long>();
                    foreach (var publicIdChunk in aiMatches.Chunk(sqlIdChunkSize))
                        matchedIds.AddRange(await records.GetIdsByPublicIdsAsync(source, publicIdChunk, ct));

                    aiSearchIdFilterChunks = matchedIds.Count == 0
                        ? new List<FilterGroup?> { new FilterGroup { Logic = "and", Nodes = [new() { Condition = new() { FieldId = 3, Operator = "eq", Value = "-1" } }] } }
                        : matchedIds.Chunk(sqlIdChunkSize)
                            .Select(idChunk => (FilterGroup?)new FilterGroup { Logic = "and", Nodes = [new() { Condition = new() { FieldId = 3, Operator = "in", Value = JsonSerializer.Serialize(idChunk) } }] })
                            .ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    services.GetService<ILogger<CopyRecordsExecutor>>()?
                        .LogWarning(ex, "[CopyRecords] Azure AI Search unavailable for table {TableId}. Falling back to SQL.", source.Id);
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
        // AI Search already fully resolved the filter (including any encrypted-field
        // conditions, against its plaintext index) — the resulting Id filter chunks need no
        // further in-memory pass. Otherwise, split as before: physical conditions to SQL,
        // computed/encrypted conditions evaluated in memory against decrypted candidate rows.
        List<FilterGroup?> physicalFilterChunks;
        FilterGroup? computedFilter;
        if (aiSearchIdFilterChunks != null)
        {
            physicalFilterChunks = aiSearchIdFilterChunks;
            computedFilter = null;
        }
        else
        {
            FilterGroup? physicalFilter;
            (physicalFilter, computedFilter) = FormulaFilterSorter.SplitFilterTree(effectiveFilter, inMemoryFilterFids);
            physicalFilterChunks = new List<FilterGroup?> { physicalFilter };
        }

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
                throw new PipelineNonRetryableException($"Copy Records: {summary.ErrorCount} {(summary.ErrorCount == 1 ? "record" : "records")} could not be copied ({summary.InsertedCount} inserted, {summary.UpdatedCount} updated). First error: {summary.Errors.FirstOrDefault()}");
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
                        var relational = await services.GetRequiredService<IRelationalProjector>().ProjectAsync(source, sourceFields, page, ct);
                        var computed = services.GetRequiredService<IFormulaProjector>().Project(sourceFields, page, relational, source);
                        var pairs = page.Select((row, i) => (Row: row, Computed: computed[i])).ToList();
                        if (computedFilter != null) pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, computedFilter, sourceFields);
                        projected = pairs.Select(pair =>
                        {
                            var row = new Dictionary<string, object?>(pair.Row);
                            foreach (var value in pair.Computed) row[PhysicalNaming.ColumnName((int)value.Key)] = value.Value;
                            return (IReadOnlyDictionary<string, object?>)row;
                        }).ToList();
                    }
                    if (projected.Count == 0) continue;
                    // Persist only exported values, not formula dependencies or hidden columns.
                    var exportColumns = new HashSet<string>(exported.Select(PhysicalNaming.GetPhysicalColumnName), StringComparer.OrdinalIgnoreCase) { "PublicId" };
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
        var remaining = TimeSpan.FromHours(1) - (DateTime.UtcNow - snapshot.StartedUtc);
        if (remaining <= TimeSpan.Zero) throw new PipelineNonRetryableException("Copy Records exceeded its one-hour execution limit.");
        timeout.CancelAfter(remaining);
        long inserted = 0, updated = 0, errors = 0;
        var messages = new List<string>();
        // The merge field stores ciphertext when encrypted, so an "=" SQL lookup per row can
        // never match. Build a decrypted key -> record index once up front instead (same
        // client-side approach RecordRepository.GetDistinctFieldValuesAsync uses for encrypted
        // fields), and keep it updated as rows are inserted so later rows in this same run still
        // see them, matching what a live per-row SQL lookup would have seen.
        var mergeIndex = merge.IsEncrypted
            ? await BuildEncryptedMergeIndexAsync(records, destination, destinationFields, merge, ct)
            : null;
        for (var pageIndex = 0; pageIndex < snapshot.Pages; pageIndex++)
        {
            var encrypted = await Read($"/snapshot/{pageIndex}") ?? throw new InvalidOperationException("Copy Records snapshot page is missing.");
            var json = await encryption.DecryptDataAsync(encrypted, snapshot.WrappedKey, queryContext.TenantId, source.AppId, ct);
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var rowKey = "/row/" + row["PublicId"].GetString();
                var cached = await Read(rowKey);
                Receipt receipt;
                if (cached != null) receipt = JsonSerializer.Deserialize<Receipt>(cached)!;
                else
                {
                    await uow.BeginAsync(ct);
                    try
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
                        values.TryGetValue(merge.Fid!.Value, out var key);
                        IReadOnlyDictionary<string, object?>? existing = null;
                        var keyStr = key != null ? Convert.ToString(key, CultureInfo.InvariantCulture) : null;
                        if (!string.IsNullOrEmpty(keyStr))
                        {
                            if (merge.IsEncrypted)
                            {
                                if (mergeIndex!.TryGetValue(keyStr, out var matches))
                                {
                                    if (matches.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
                                    existing = matches[0];
                                }
                            }
                            else
                            {
                                var match = new FilterGroup
                                {
                                    Nodes = new() { new() { Condition = new()
                                { FieldId = merge.Fid.Value, Operator = "eq", Value = keyStr } } }
                                };
                                var found = await records.ListAsync(destination, destinationFields, page: 1, pageSize: 2, filterTree: match, ct: ct);
                                if (found.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
                                existing = found.FirstOrDefault();
                            }
                        }
                        foreach (var systemField in destinationFields.Where(f => f.IsSystem && f.Fid.HasValue))
                            values.Remove(systemField.Fid!.Value); // System identities are match-only, never writable.
                        if (existing != null)
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
                                "Record updated via Pipeline Copy Records", ct, uow.Transaction);
                            receipt = new(false, true);
                        }
                        else
                        {
                            foreach (var field in destinationFields.Where(f => !f.IsSystem && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
                            {
                                if (!values.ContainsKey(field.Fid!.Value) && !string.IsNullOrWhiteSpace(field.DefaultValue))
                                    values[field.Fid.Value] = await DefaultValue(field, queryContext, ct);
                            }
                            var overrides = await ReferenceWriteValidator.ValidateAsync(destinationFields, values, tableRepo, fieldRepo, records, relRepo, ct);
                            foreach (var pair in overrides) values[pair.Key] = pair.Value;
                            await UserFieldValueResolver.ResolveAsync(services.GetRequiredService<IUserRepository>(), destinationFields, values, ct);
                            await RecordConstraintValidator.ValidateAsync(destination, destinationFields, values, records, true, null, ct);
                            await CustomDataRuleValidator.ValidateAsync(destination, destinationFields, values, tableRepo, fieldRepo, records, services.GetRequiredService<FormulaEngine>(), ct);
                            var publicId = await records.CreateAsync(destination, destinationFields, values, uow.Transaction, ct);
                            var identityField = destinationFields.SingleOrDefault(f => f.IsSystem && f.PhysicalColumnName == "Id" && f.Fid.HasValue);
                            if (identityField != null)
                                values[identityField.Fid!.Value] = await records.GetActiveRecordIdByPublicIdAsync(destination, publicId, uow.Transaction, ct);
                            // Date Created / Record Owner were stripped from values above (system
                            // identities are match-only, never writable), so a pipeline trigger
                            // firing off this copy-created record could never resolve
                            // {{steps.<trigger>.fid_N}} for them. Same fix as
                            // CreateRecordCommandHandler's analogous backfill.
                            var copyCreatedOnField = destinationFields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedOn" && f.Fid.HasValue);
                            if (copyCreatedOnField != null) values[copyCreatedOnField.Fid!.Value] = DateTime.UtcNow;
                            var copyCreatedByField = destinationFields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
                            if (copyCreatedByField != null) values[copyCreatedByField.Fid!.Value] = queryContext.UserId;
                            await services.GetRequiredService<IAuditRepository>().LogActivityAsync(AuditActions.Created, AuditEntityTypes.Record,
                                publicId.ToString(), "Record created via Pipeline Copy Records", appId: destination.AppId, ct: ct);
                            await tableRepo.IncrementRecordCountAsync(destination.Id, ct);
                            await services.GetRequiredService<IPipelineTriggerInterceptor>().InterceptAsync(destination, destinationFields, publicId, values, "record-added", ct);
                            if (merge.IsEncrypted && keyStr != null)
                            {
                                if (!mergeIndex!.TryGetValue(keyStr, out var list))
                                    mergeIndex[keyStr] = list = new List<IReadOnlyDictionary<string, object?>>();
                                list.Add(new Dictionary<string, object?> { ["PublicId"] = publicId });
                            }
                            receipt = new(true, false);
                        }
                        await Store(rowKey, JsonSerializer.Serialize(receipt));
                        await uow.CommitAsync(ct);
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
                }
                if (receipt.Inserted) inserted++;
                if (receipt.Updated) updated++;
                if (receipt.Error != null) { errors++; if (messages.Count < 20) messages.Add(receipt.Error); }
            }
        }
        var output = JsonSerializer.Serialize(new Summary(inserted, updated, errors, messages));
        await uow.BeginAsync(ct);
        try { await Store("/complete", output); await uow.CommitAsync(ct); }
        catch { await uow.RollbackAsync(CancellationToken.None); throw; }
        return Finish(output);
    }

    /// <summary>Scans the destination table (paginated, decrypted) once and indexes it by the
    /// encrypted merge field's plaintext value, case-insensitively, so per-row merge matching
    /// during Copy Records doesn't run an "=" SQL comparison against ciphertext (which can never
    /// match) and doesn't re-scan the whole table per row.</summary>
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
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<IReadOnlyDictionary<string, object?>>();
                list.Add(row);
            }
            if (rows.Count < pageSize) break;
        }
        return index;
    }
}
