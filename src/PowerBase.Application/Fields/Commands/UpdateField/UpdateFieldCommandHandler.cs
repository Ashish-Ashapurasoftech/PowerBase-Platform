using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.SnapshotJobs;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Commands.UpdateField;

public class UpdateFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly FieldSettingsGuard _guard;
    private readonly FieldVersionService _versionService;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;
    private readonly IAzureSearchService _searchService;
    private readonly IRelationshipRepository _relRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IAppVariableRepository? _variableRepo;
    private readonly FieldSnapshotJobService? _snapshotJobs;
    private readonly ILogger<UpdateFieldCommandHandler>? _logger;
    private readonly ISearchFieldJobs? _fieldJobs;

    /// <summary>The Number/Currency/Percent/Rating family — the only TypeCodes a field's
    /// "Display As" Behavior Setting is allowed to switch between (see NumericDisplayAs).</summary>
    private static readonly string[] NumericFamilyTypeCodes = ["Number", "Currency", "Percent", "Rating"];

    public UpdateFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo,
        ISchemaEngineService schemaEngine,
        FieldSettingsGuard guard,
        FieldVersionService versionService,
        ITenantUnitOfWork uow,
        IFieldTypeRepository fieldTypeRepo,
        IMessagePublisher messagePublisher,
        IQueryContext queryContext,
        IAzureSearchService searchService,
        IRelationshipRepository relRepo,
        IFieldReferenceIndexer? refIndexer = null,
        IAppVariableRepository? variableRepo = null,
        FieldSnapshotJobService? snapshotJobs = null,
        ILogger<UpdateFieldCommandHandler>? logger = null,
        ISearchFieldJobs? fieldJobs = null)
    {
        _snapshotJobs = snapshotJobs;
        _logger = logger;
        _fieldJobs = fieldJobs;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
        _variableRepo = variableRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        _schemaEngine = schemaEngine;
        _guard = guard;
        _versionService = versionService;
        _uow = uow;
        _fieldTypeRepo = fieldTypeRepo;
        _messagePublisher = messagePublisher;
        _queryContext = queryContext;
        _searchService = searchService;
        _relRepo = relRepo;
    }

    public async Task HandleAsync(UpdateFieldCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Label))
            throw new ValidationException(new Dictionary<string, string[]> { ["Label"] = ["Label is required."] });

        if (string.IsNullOrWhiteSpace(command.CommitMessage))
            throw new ValidationException(new Dictionary<string, string[]> { ["CommitMessage"] = ["A reason for this change is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Load the existing field to:
        //   (a) get its TypeCode for Settings validation,
        //   (b) detect the optional→required transition (for NULL backfill),
        //   (c) enforce the required+None+default invariant.
        var existing = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
                       ?? throw new NotFoundException("Field", command.FieldPublicId);

        // Defend against a caller supplying a field PublicId from a different table than the one
        // named in the route (and thus than the one the permission check above just authorized).
        if (existing.AppTableId != table.Id)
            throw new NotFoundException("Field", command.FieldPublicId);

        var before = FieldSnapshot.From(existing);

        // System fields (Record ID#, Date Created/Modified, Record Owner, Last Modified By) only
        // expose a reduced settings surface on the Field Detail page — Label stays read-only, only
        // Searchable/Reportable stay togglable, and each type's Behavior Settings collapse to a
        // fixed allow-list (see SystemFieldSettingsPolicy). Coerced here, before every check below,
        // so the request body is never trusted for anything beyond what the UI actually offers —
        // this is the authoritative enforcement; the frontend hiding these controls is only UX.
        // Leading/trailing whitespace is never meaningful here — trim server-side so a client that
        // bypasses the UI (a direct API call) can't persist " Full Name " verbatim. NullIfBlank also
        // collapses a whitespace-only Description down to "unset", matching what omitting it
        // altogether means.
        var label = existing.IsSystem ? existing.Label! : command.Label.Trim();
        var description = existing.IsSystem ? existing.Description : NullIfBlank(command.Description);
        var isRequired = existing.IsSystem ? false : command.IsRequired;
        var defaultValue = existing.IsSystem ? null : command.DefaultValue;
        var isUnique = existing.IsSystem ? false : command.IsUnique;
        var isSortable = existing.IsSystem ? false : command.IsSortable;
        var isFilterable = existing.IsSystem ? false : command.IsFilterable;
        var isAuditable = existing.IsSystem ? false : command.IsAuditable;
        var isEncrypted = existing.IsSystem || PowerBase.Domain.Constants.PhysicalNaming.IsEncryptionExemptTypeCode(existing.TypeCode)
            ? false
            : command.IsEncrypted;
        var snapshotSourceFid = existing.IsSystem ? null : command.SnapshotSourceFid;
        var snapshotEditable = snapshotSourceFid.HasValue && command.SnapshotEditable;
        var isAutoFill = existing.IsSystem || snapshotSourceFid.HasValue ? false : command.IsAutoFill;
        var isSharedValues = existing.IsSystem ? false : command.IsSharedValues ?? existing.IsSharedValues;
        var settings = existing.IsSystem
            ? SystemFieldSettingsPolicy.RestrictSettingsJson(existing.TypeCode, command.Settings)
            // A request that sends no settings leaves them as they are: UpdateFieldSql writes Settings = @settings,
            // so passing null on would silently erase a formula's expression (or any other saved setting).
            : command.Settings ?? existing.Settings;

        if (!existing.IsSystem && PowerBase.Formula.Binding.FieldIdReference.IsReservedLabel(label))
            throw new ValidationException(new Dictionary<string, string[]> { ["Label"] = [CreateField.CreateFieldCommandValidator.ReservedLabelMessage(label)] });

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, excludeFieldId: existing.Id, ct: ct))
            throw new DuplicateException("Field", "label", label);

        _guard.ValidateSettingsAndCapabilities(
            existing.TypeCode, settings, settings ?? existing.Settings, label, isRequired, isUnique, defaultValue, isAutoFill);

        var tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct) ?? Array.Empty<AppField>();
        var sharedErrors = FieldSharedValuesCapability.Validate(existing.TypeCode, label, existing.IsSystem, isSharedValues && !existing.IsSharedValues);
        if (sharedErrors.Count > 0) throw new ValidationException(new Dictionary<string, string[]>(sharedErrors));
        _guard.ValidateActionButtonTargets(existing.TypeCode, settings, tableFields, existing.Id);
        _guard.ValidateSnapshot(existing, label, snapshotSourceFid, snapshotEditable, isRequired, isUnique, command.IsAutoFill && snapshotSourceFid.HasValue, tableFields);

        // Formula text is stored by field ID so a rename can't break it (see FormulaCanonicalizer).
        // The label being saved right now is also accepted as a name, so a formula can reference a
        // field by the new label in the same save that renames it.
        if (settings is not null)
        {
            var renamedTo = existing.Fid.HasValue && !string.Equals(label, existing.Label, StringComparison.Ordinal)
                ? new Dictionary<string, long> { [label] = existing.Fid.Value }
                : null;
            settings = new FormulaCanonicalizer(tableFields, renamedTo, await FormulaVariables.LoadAsync(_variableRepo, table.AppId, ct))
                .ToStoredSettings(existing.TypeCode, settings);
        }

        // ── Numeric family "Display As" type switch ─────────────────────────────
        // Number/Currency/Percent/Rating share one settings shape (NumericSettings) with a
        // DisplayAs member. When it names a different TypeCode within that same family, the
        // field's actual type changes to match — e.g. a Percent field whose Display As is
        // switched to Currency becomes a Currency field. TypeCode is a pure FieldTypeId swap
        // (see AppFieldRepository.UpdateFieldTypeAsync) — never a physical-column change,
        // except for the one narrow, always-lossless INT-to-DECIMAL widening below (only ever
        // needed for a legacy Rating field created before Rating's catalog type became
        // DECIMAL(18,4) — see database/migrations/tenant/045_alter_fieldtype_rating_decimal.sql).
        var percentRescaled = false;
        if (NumericFamilyTypeCodes.Contains(existing.TypeCode) && !string.IsNullOrWhiteSpace(settings))
        {
            NumericSettings? numericSettings = null;
            try
            {
                numericSettings = JsonSerializer.Deserialize<NumericSettings>(
                    settings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { /* already rejected above by _settingsRegistry.Validate */ }

            // A system field's DisplayAs was already stripped by SystemFieldSettingsPolicy above,
            // so this branch is naturally a no-op for Record ID# — the type-switch feature only
            // ever applies to custom Numeric-family fields.
            if (numericSettings?.DisplayAs is string displayAs
                && NumericFamilyTypeCodes.Contains(displayAs)
                && !string.Equals(displayAs, existing.TypeCode, StringComparison.OrdinalIgnoreCase))
            {
                var targetFieldType = await _fieldTypeRepo.GetByCodeAsync(displayAs, ct)
                    ?? throw new NotFoundException("FieldType", displayAs);

                // Refuse the switch if a Summary field built on this field couldn't aggregate the new type.
                await SummaryDependencyGuard.EnsureTypeChangeKeepsSummariesValidAsync(
                    existing, displayAs, _relRepo, _tableRepo, _fieldRepo, ct);

                // Bring a legacy INT (pre-migration Rating) column up to DECIMAL(18,4) first —
                // a no-op for every field created after that migration, since Number/Currency/
                // Percent/Rating all already share that physical type.
                await _schemaEngine.WidenIntColumnToDecimalIfNeededAsync(table, existing, ct);

                // Percent is stored as a whole number (115 = 115%), every other numeric type as the
                // raw quantity (1.15). So switching INTO Percent multiplies the stored values by 100
                // and switching OUT of it divides by 100 — otherwise 1.15 would read back as 1.15%.
                var toPercent = string.Equals(displayAs, "Percent", StringComparison.OrdinalIgnoreCase);
                var fromPercent = string.Equals(existing.TypeCode, "Percent", StringComparison.OrdinalIgnoreCase);
                if (toPercent || fromPercent)
                {
                    // An encrypted column can't be updated with server-side arithmetic.
                    if (existing.IsEncrypted)
                        throw new ValidationException(new Dictionary<string, string[]>
                        {
                            ["Settings"] = ["An encrypted field can't be switched to or from Percent. Turn encryption off first."]
                        });

                    await _schemaEngine.ScaleNumericColumnForPercentAsync(table, existing, toPercent, ct);
                    try
                    {
                        await _fieldRepo.UpdateFieldTypeAsync(existing.Id, targetFieldType.Id, settings, isRequired, ct);
                    }
                    catch
                    {
                        // Best effort: put the values back so data and field type stay in agreement.
                        await _schemaEngine.ScaleNumericColumnForPercentAsync(table, existing, !toPercent, ct);
                        throw;
                    }
                    percentRescaled = true;
                }
                else
                {
                    await _fieldRepo.UpdateFieldTypeAsync(existing.Id, targetFieldType.Id, settings, isRequired, ct);
                }
            }
        }

        await _guard.ValidateRequiredHasDefaultOrNoRestrictedRolesAsync(existing.Id, label, isRequired, defaultValue, ct);
        await _guard.ValidateUniqueTransitionAsync(table, existing, label, isUnique, ct);
        await _guard.ValidateEncryptionTransitionAsync(table, existing, isEncrypted, ct);

        // Ciphertext needs a text column (see SchemaEngineService.AddColumnAsync); a field created
        // unencrypted has its type's native column (DATE, DECIMAL, …), so re-type it to match the new
        // flag. Runs before SetUniqueAsync below, which re-creates the unique index it drops.
        if (isEncrypted != existing.IsEncrypted)
            await _schemaEngine.SetColumnEncryptionAsync(table, existing, isEncrypted, ct);

        // Build the physical constraint before persisting IsUnique. A failed index build must
        // not leave metadata claiming that uniqueness is enforced. Also repair fields whose
        // metadata was saved by an earlier failed attempt but whose index is missing.
        if (isUnique || existing.IsUnique)
            await _schemaEngine.SetUniqueAsync(table, existing, isUnique, ct);

        // Save old search eligibility before UpdateAsync modifies metadata
        bool wasSearchable = existing.IsSearchable;
        bool wasFilterable = existing.IsFilterable;
        bool wasEligible = wasSearchable || wasFilterable;
        bool isEligible = command.IsSearchable || isFilterable;

        var after = new FieldSnapshot(
            label, description, isRequired, defaultValue, command.IsSearchable, isSortable,
            isFilterable, command.IsReportable, isAuditable, isUnique, isEncrypted, isAutoFill, settings,
            snapshotSourceFid, snapshotEditable, isSharedValues);

        // The field row itself and its new version are one atomic unit: either both land or
        // neither does, so a version is never created for a field-settings change that didn't
        // actually take effect (and vice versa).
        await _uow.BeginAsync(ct);
        try
        {
            var affected = await _fieldRepo.UpdateAsync(
                existing.PublicId, table.Id,
                label, description,
                isRequired, defaultValue,
                command.IsSearchable, isSortable,
                isFilterable, command.IsReportable, isAuditable,
                isUnique, isEncrypted, isAutoFill, settings, ct, _uow.Transaction);

            if (affected == 0)
                throw new NotFoundException("Field", command.FieldPublicId);

            if (existing.SnapshotSourceFid != snapshotSourceFid || existing.SnapshotEditable != snapshotEditable)
                await _fieldRepo.SetSnapshotConfigAsync(existing.Id, snapshotSourceFid, snapshotEditable, ct, _uow.Transaction);

            if (existing.IsSharedValues != isSharedValues)
                await _fieldRepo.SetSharedValuesAsync(existing.Id, isSharedValues, ct, _uow.Transaction);

            await _versionService.CreateVersionIfChangedAsync(
                existing.Id, before, after, command.CommitMessage,
                FieldVersionChangeType.Update, restoredFromVersion: null, _uow.Transaction!, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        // Backfill: when an optional field becomes required and a default is supplied, fill existing
        // rows whose value is NULL/empty so they remain valid.
        if (isRequired && !string.IsNullOrWhiteSpace(defaultValue) && !existing.IsRequired)
        {
            existing.IsRequired = isRequired;
            existing.DefaultValue = defaultValue;
            await _recordRepo.BackfillDefaultAsync(table, existing, defaultValue!, ct);
        }

        // Search Index Sync: when search eligibility (IsSearchable || IsFilterable) changes, trigger a backfill or nullify. Searchable
        // alone changing on a field that stays filterable also rewrites the records: it adds or removes the field's text in quick search.
        if ((isEligible != wasEligible || (isEligible && command.IsSearchable != wasSearchable)) && existing.Fid.HasValue)
        {
            await SyncSearchIndexAsync(table, existing.Fid.Value, isNullify: !isEligible, ct);
        }
        else if (percentRescaled && isEligible && existing.Fid.HasValue)
        {
            // The stored values changed (x100 or /100), so the already-indexed copies are stale.
            await SyncSearchIndexAsync(table, existing.Fid.Value, isNullify: false, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, existing.PublicId.ToString(),
            $"Field modified: {label} In TableName : {table.Name}", appId: table.AppId, ct: ct);

        // "Initialize field for existing records": captures the snapshot for every record that already exists, as a background job.
        if (command.InitializeSnapshotForExisting && snapshotSourceFid.HasValue && _snapshotJobs is not null)
        {
            existing.SnapshotSourceFid = snapshotSourceFid;
            existing.SnapshotEditable = snapshotEditable;
            await _snapshotJobs.StartAsync(table, existing, ct);
        }

        // Formulas bind by name or label, so renaming this field can change what OTHER fields' formulas read.
        if (!string.Equals(label, existing.Label, StringComparison.Ordinal))
            await _refIndexer.ReindexTableFieldsAsync(table.Id, ct);
        else
            await _refIndexer.ReindexFieldAsync(existing.PublicId, ct);
    }

    /// <summary>The field is saved by now: this never throws, so the audit entry and formula reindex below still run if the index fails.</summary>
    private Task SyncSearchIndexAsync(AppTable table, int fid, bool isNullify, CancellationToken ct) =>
        PowerBase.Application.Search.FieldSlotSync.SyncAsync(
            _fieldRepo, _recordRepo, _searchService, _messagePublisher, _queryContext.TenantId, table.AppId, table.Id, fid, isNullify,
            clearSlot: null, (ex, message) => _logger?.LogError(ex, "{Message}", message), ct, _fieldJobs);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
