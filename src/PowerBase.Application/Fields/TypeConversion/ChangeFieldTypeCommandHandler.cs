using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.TypeConversion;

public sealed record ChangeFieldTypeCommand(Guid TablePublicId, Guid FieldPublicId, string TargetTypeCode, string? CommitMessage);

/// <summary>
/// Changes a field's type (docs/field-type-conversion.md). Order matters:
///  1. load + check (matrix, blockers: system/key/encrypted/Summary) — nothing written if anything blocks;
///  2. dry run — decides Unique (duplicates turn it off) and SingleSelect choices before data moves;
///  3. one transaction: physical column conversion + the field's new FieldTypeId/Settings/Default/Unique;
///  4. dependents: Lookup/Summary stored source types follow the new type;
///  5. Unique index rebuilt, search index refreshed, audit logged, usage index refreshed.
/// Records are never deleted; values that can't be converted become blank.
/// </summary>
public sealed class ChangeFieldTypeCommandHandler
{
    private readonly FieldTypeChangePlanner _planner;
    private readonly FieldTypeChangeAnalyzer _analyzer;
    private readonly IFieldColumnConversionService _columns;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IRecordRepository _recordRepo;
    private readonly IAzureSearchService _searchService;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IRelationshipRepository? _relRepo;

    public ChangeFieldTypeCommandHandler(
        FieldTypeChangePlanner planner, FieldTypeChangeAnalyzer analyzer, IFieldColumnConversionService columns,
        IAppFieldRepository fieldRepo, ISchemaEngineService schemaEngine, IRecordRepository recordRepo,
        IAzureSearchService searchService, IMessagePublisher messagePublisher, IQueryContext queryContext,
        IAuditRepository auditRepo, IFieldReferenceIndexer? refIndexer = null, IRelationshipRepository? relRepo = null)
    {
        _relRepo = relRepo;
        _planner = planner;
        _analyzer = analyzer;
        _columns = columns;
        _fieldRepo = fieldRepo;
        _schemaEngine = schemaEngine;
        _recordRepo = recordRepo;
        _searchService = searchService;
        _messagePublisher = messagePublisher;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
    }

    public async Task<ChangeFieldTypeResultDto> HandleAsync(ChangeFieldTypeCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.CommitMessage))
            throw new ValidationException(new Dictionary<string, string[]> { ["commitMessage"] = ["A reason for this change is required."] });

        // 1. Load and check.
        var plan = await _planner.BuildAsync(command.TablePublicId, command.FieldPublicId, command.TargetTypeCode, ct);
        if (!plan.Check.IsAllowed)
            throw new ValidationException(new Dictionary<string, string[]> { ["targetTypeCode"] = [plan.Check.BlockedReason!] });

        var field = plan.Field;
        var kind = plan.Check.Kind!.Value;
        var targetType = plan.TargetType!;
        var impact = await _analyzer.AnalyzeAsync(plan.App, plan.Table, field, plan.TableFields, plan.TargetTypeCode,
            FieldTypeChangeSettings.Build(field, plan.TargetTypeCode, kind), ct);
        if (impact.Blockers.Count > 0)
            throw new ConflictException(string.Join(" ", impact.Blockers));

        // 2. Dry run: Unique and SingleSelect choices are settled before any data moves.
        var warnings = new List<string>();
        var dryRun = field.IsUnique || plan.TargetTypeCode == "SingleSelect"
            ? await FieldTypeChangeDryRun.RunAsync(plan, _columns, sampleSize: 0, ct)
            : null;

        // Same per-type rules the field page enforces (FieldGeneralSettingsCapability). A formula has no column, so
        // it can't hold a unique index here.
        var capabilities = FieldGeneralSettingsCapability.Resolve(plan.TargetTypeCode, null);
        var targetIsFormula = FieldTypeConversionMatrix.IsFormula(plan.TargetTypeCode);
        var keepUnique = field.IsUnique && !targetIsFormula && capabilities?.Unique != false && (dryRun?.DuplicateValues ?? 0) == 0;
        var keepRequired = field.IsRequired && capabilities?.Required != false;
        if (field.IsUnique && !keepUnique)
            warnings.Add("\"Must be unique\" was turned off: the converted values repeat or the new type can't be unique.");
        if (field.IsRequired && !keepRequired)
            warnings.Add("\"Must be filled in\" was turned off: the new type can't be required.");
        if (dryRun?.Choices is { Overflowed: true } overflow)
            warnings.Add($"Only the first {SelectChoiceBuilder.MaxChoices} distinct values became choices; {overflow.OverflowCount} more were kept on their records.");

        var metadata = new FieldTypeMetadata(
            targetType.Id,
            targetType.SqlDataType,
            FieldTypeChangeSettings.Build(field, plan.TargetTypeCode, kind, dryRun?.Choices?.Choices),
            capabilities?.Default == false ? null : FieldTypeChangeSettings.BuildDefaultValue(field, plan.TargetTypeCode, plan.ConversionContext),
            keepUnique,
            keepRequired);

        // 3. Data + field type, all-or-nothing.
        var result = kind switch
        {
            ConversionKind.ScalarToScalar or ConversionKind.NumericFamily =>
                await _columns.ConvertAsync(plan.Table, field, metadata, plan.Convert, ct),
            ConversionKind.FormulaToScalar =>
                await _columns.CreateFromValuesAsync(plan.Table, field, metadata,
                    (plan.FormulaValues ?? new Dictionary<long, object?>()).ToDictionary(p => p.Key, p => plan.Convert(p.Value).Value), ct),
            ConversionKind.ScalarToFormula =>
                await _columns.DropAsync(plan.Table, field, metadata, ct),
            _ => await ApplyMetadataOnlyAsync(field, metadata, ct),
        };
        var valuesBlanked = kind == ConversionKind.FormulaToScalar
            ? plan.FormulaValues!.Count(p => plan.Convert(p.Value).Lost)
            : result.ValuesLost;

        // 4. Dependents that keep a copy of the source type.
        foreach (var update in impact.LookupUpdates.Concat(impact.SummaryUpdates))
            await _fieldRepo.UpdateSettingsAsync(update.Field.Id, update.NewSettings, ct);
        if (impact.BrokenFormulas.Count > 0)
            warnings.Add($"{impact.BrokenFormulas.Count} formula(s) no longer work with the new type: "
                         + string.Join("; ", impact.BrokenFormulas.Select(b => $"{b.TableName} › {b.FieldLabel}")) + ".");

        // A formula that was a reference proxy can't stay one once it is a stored field: the role moves to the
        // relationship's next lookup (or is cleared).
        if (kind == ConversionKind.FormulaToScalar && _relRepo is not null)
            await PowerBase.Application.Relationships.ReferenceProxyRules.ReassignIfRemovedAsync(
                plan.Table.Id, [field.Id], _relRepo, _fieldRepo, ct);

        // 5. Unique index, search, audit, usage index.
        var converted = await _fieldRepo.GetByPublicIdAsync(field.PublicId, ct) ?? field;

        // Lookups (and lookups of lookups) save the type of value they show; they follow the field's new type, so e.g. a Snapshot
        // can still tell what a lookup of this field holds. Only the source type copy was updated above.
        if (_relRepo is not null && PowerBase.Application.Relationships.FieldValueTypes.Of(converted) is { } newValueType)
            await PowerBase.Application.Relationships.FieldValueTypes.RefreshLookupsOfAsync(converted, newValueType, _relRepo, _fieldRepo, ct);

        if (keepUnique)
        {
            try
            {
                await _schemaEngine.SetUniqueAsync(plan.Table, converted, true, ct);
            }
            catch (Exception ex)
            {
                await _columns.ApplyMetadataAsync(converted, metadata with { IsUnique = false }, ct);
                warnings.Add("\"Must be unique\" was turned off: the unique index couldn't be rebuilt.");
                Console.WriteLine($"[ChangeFieldType] unique index for field {field.Id} not rebuilt: {ex.Message}");
            }
        }

        if (field.IsSearchable && field.Fid is int fid && !PhysicalNaming.IsComputedTypeCode(plan.TargetTypeCode))
            await RefreshSearchIndexAsync(plan.Table, fid, ct);

        var label = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label!;
        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(),
            $"Field type changed: {label} from {field.TypeCode} to {plan.TargetTypeCode} in TableName : {plan.Table.Name}",
            appId: plan.Table.AppId,
            oldValues: System.Text.Json.JsonSerializer.Serialize(new { typeCode = field.TypeCode, settings = field.Settings, field.DefaultValue, field.IsUnique }),
            newValues: System.Text.Json.JsonSerializer.Serialize(new
            {
                typeCode = plan.TargetTypeCode, settings = metadata.Settings, metadata.DefaultValue, metadata.IsUnique,
                recordsScanned = result.RowsScanned, valuesKept = result.ValuesKept, valuesBlanked, reason = command.CommitMessage,
            }),
            ct: ct);

        await _refIndexer.ReindexFieldAsync(field.PublicId, ct);

        return new ChangeFieldTypeResultDto(field.TypeCode, plan.TargetTypeCode, result.RowsScanned, result.ValuesKept, valuesBlanked, warnings);
    }

    private async Task<ColumnConversionResult> ApplyMetadataOnlyAsync(AppField field, FieldTypeMetadata metadata, CancellationToken ct)
    {
        await _columns.ApplyMetadataAsync(field, metadata, ct);
        return new ColumnConversionResult(0, 0, 0, []);
    }

    /// <summary>Stored values changed, so their indexed copies are stale — same refresh UpdateFieldCommandHandler
    /// does after a Percent rescale.</summary>
    private async Task RefreshSearchIndexAsync(AppTable table, int fid, CancellationToken ct)
    {
        try
        {
            var docs = await _recordRepo.GetFieldBackfillBatchAsync(_queryContext.TenantId, table.AppId, table.Id, fid, false, page: 1, pageSize: 500, ct);
            if (docs.Count > 0) await _searchService.BulkIndexRecordsAsync(docs, ct);
            _ = _messagePublisher.PublishAsync(new PowerBase.Application.Common.Models.SearchIndexMessage
            {
                Action = PowerBase.Application.Common.Models.IndexAction.BackfillField,
                TenantId = _queryContext.TenantId, AppId = table.AppId, TableId = table.Id, FieldId = fid, Page = 1,
            }, default);
        }
        catch (Exception ex)
        {
            // The type change itself succeeded; search catches up on the next backfill.
            Console.WriteLine($"[ChangeFieldType] search refresh for table {table.Id} field {fid} failed: {ex.Message}");
        }
    }
}
