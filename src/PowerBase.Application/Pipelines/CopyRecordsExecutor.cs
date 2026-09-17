using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        var computedFids = sourceFields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode)).Select(f => (long)f.Fid!.Value).ToHashSet();
        var (physicalFilter, computedFilter) = FormulaFilterSorter.SplitFilterTree(effectiveFilter, computedFids);

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
                throw new PipelineNonRetryableException($"Copy Records completed with {summary.ErrorCount} user error(s); {summary.InsertedCount} inserted, {summary.UpdatedCount} updated. {summary.Errors.FirstOrDefault()}");
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
                await foreach (var page in search.ReadCopySnapshotAsync(source, snapshotFields, physicalFilter, ct))
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
                                throw CopyRecordsDefinition.Error($"Source field '{field.Name}' has no exported value.");
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
                        if (key != null && !string.IsNullOrEmpty(Convert.ToString(key, CultureInfo.InvariantCulture)))
                        {
                            var match = new FilterGroup
                            {
                                Nodes = new() { new() { Condition = new()
                            { FieldId = merge.Fid.Value, Operator = "eq", Value = Convert.ToString(key, CultureInfo.InvariantCulture) } } }
                            };
                            var found = await records.ListAsync(destination, destinationFields, page: 1, pageSize: 2, filterTree: match, ct: ct);
                            if (found.Count > 1) throw CopyRecordsDefinition.Error("The destination merge value matches more than one record.");
                            existing = found.FirstOrDefault();
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
                            var overrides = await ReferenceWriteValidator.ValidateAsync(destinationFields, values, tableRepo, fieldRepo, records, ct);
                            foreach (var pair in overrides) values[pair.Key] = pair.Value;
                            await UserFieldValueResolver.ResolveAsync(services.GetRequiredService<IUserRepository>(), destinationFields, values, ct);
                            await RecordConstraintValidator.ValidateAsync(destination, destinationFields, values, records, true, null, ct);
                            await CustomDataRuleValidator.ValidateAsync(destination, destinationFields, values, tableRepo, fieldRepo, records, services.GetRequiredService<FormulaEngine>(), ct);
                            var publicId = await records.CreateAsync(destination, destinationFields, values, uow.Transaction, ct);
                            var identityField = destinationFields.SingleOrDefault(f => f.IsSystem && f.PhysicalColumnName == "Id" && f.Fid.HasValue);
                            if (identityField != null)
                                values[identityField.Fid!.Value] = await records.GetActiveRecordIdByPublicIdAsync(destination, publicId, uow.Transaction, ct);
                            await services.GetRequiredService<IAuditRepository>().LogActivityAsync(AuditActions.Created, AuditEntityTypes.Record,
                                publicId.ToString(), "Record created via Pipeline Copy Records", appId: destination.AppId, ct: ct);
                            await tableRepo.IncrementRecordCountAsync(destination.Id, ct);
                            await services.GetRequiredService<IPipelineTriggerInterceptor>().InterceptAsync(destination, destinationFields, publicId, values, "record-added", ct);
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
}
