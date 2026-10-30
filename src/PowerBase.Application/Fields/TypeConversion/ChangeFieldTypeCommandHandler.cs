using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Search;
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
    private readonly ILogger<ChangeFieldTypeCommandHandler>? _logger;
    private readonly ISearchFieldJobs? _fieldJobs;

    public ChangeFieldTypeCommandHandler(
        FieldTypeChangePlanner planner, FieldTypeChangeAnalyzer analyzer, IFieldColumnConversionService columns,
        IAppFieldRepository fieldRepo, ISchemaEngineService schemaEngine, IRecordRepository recordRepo,
        IAzureSearchService searchService, IMessagePublisher messagePublisher, IQueryContext queryContext,
        IAuditRepository auditRepo, IFieldReferenceIndexer? refIndexer = null, IRelationshipRepository? relRepo = null,
        ILogger<ChangeFieldTypeCommandHandler>? logger = null, ISearchFieldJobs? fieldJobs = null)
    {
        _logger = logger;
        _fieldJobs = fieldJobs;
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

        // The index column(s) the field uses now: a change to another column family (Text to Number, say) moves it to a new column,
        // and the old one, which still holds the old values in every document, must be emptied before another field can use it.
        SlotRef? oldSlot = null;
        if (field.Fid is int oldFid)
            SearchTableSlotInfo.From(plan.Table.Id, plan.TableFields).StoredSlots.TryGetValue(oldFid, out oldSlot);

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

        if ((field.IsSearchable || field.IsFilterable) && field.Fid is int fid)
            await RefreshSearchIndexAsync(plan.Table, fid, plan.TargetTypeCode, oldSlot, ct);

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

    /// <summary>Stored values changed, so their indexed copies are stale — same refresh UpdateFieldCommandHandler does after a Percent
    /// rescale, through the same slots and documents the indexer writes. A field that became a formula (no stored value) or a time of
    /// day (not indexed) is cleared instead, and a field that moved to another column has its old column emptied. Never throws: the
    /// type change itself succeeded.</summary>
    private Task RefreshSearchIndexAsync(AppTable table, int fid, string targetTypeCode, SlotRef? oldSlot, CancellationToken ct)
    {
        var notIndexed = PhysicalNaming.IsComputedTypeCode(targetTypeCode) || !SearchSlotResolver.IsIndexableType(targetTypeCode);
        return FieldSlotSync.SyncAsync(
            _fieldRepo, _recordRepo, _searchService, _messagePublisher, _queryContext.TenantId, table.AppId, table.Id, fid,
            isNullify: notIndexed, clearSlot: oldSlot, (ex, message) => _logger?.LogError(ex, "{Message}", message), ct, _fieldJobs);
    }
}
