using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Commands.UpdateField;

public class UpdateFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly IFieldTypeRepository _fieldTypeRepo;
    private readonly FieldSettingsValidatorRegistry _settingsRegistry;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;
    private readonly IAzureSearchService _searchService;

    /// <summary>The Number/Currency/Percent/Rating family — the only TypeCodes a field's
    /// "Display As" Behavior Setting is allowed to switch between (see NumericDisplayAs).</summary>
    private static readonly string[] NumericFamilyTypeCodes = ["Number", "Currency", "Percent", "Rating"];

    public UpdateFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo,
        ISchemaEngineService schemaEngine,
        FieldSettingsValidatorRegistry settingsRegistry,
        IFieldTypeRepository fieldTypeRepo,
        IMessagePublisher messagePublisher,
        IQueryContext queryContext,
        IAzureSearchService searchService)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _permRepo = permRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        _schemaEngine = schemaEngine;
        _fieldTypeRepo = fieldTypeRepo;
        _settingsRegistry = settingsRegistry;
        _messagePublisher = messagePublisher;
        _queryContext = queryContext;
        _searchService = searchService;
    }

    public async Task HandleAsync(UpdateFieldCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Label))
            throw new ValidationException(new Dictionary<string, string[]> { ["Label"] = ["Label is required."] });

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

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, command.Label, excludeFieldId: existing.Id, ct: ct))
            throw new DuplicateException("Field", "label", command.Label);

        // Validate per-type Settings JSON against the field's current type.
        var settingsErrors = _settingsRegistry.Validate(existing.TypeCode, command.Settings);
        if (settingsErrors.Count > 0)
            throw new ValidationException(settingsErrors.AsReadOnly());

        var capErrors = FieldGeneralSettingsCapability.Validate(
            existing.TypeCode, command.Settings ?? existing.Settings, command.Label,
            command.IsRequired, command.IsUnique, command.DefaultValue);
        if (capErrors.Count > 0)
            throw new ValidationException(capErrors.AsReadOnly());

        // ── Numeric family "Display As" type switch ─────────────────────────────
        // Number/Currency/Percent/Rating share one settings shape (NumericSettings) with a
        // DisplayAs member. When it names a different TypeCode within that same family, the
        // field's actual type changes to match — e.g. a Percent field whose Display As is
        // switched to Currency becomes a Currency field. TypeCode is a pure FieldTypeId swap
        // (see AppFieldRepository.UpdateFieldTypeAsync) — never a physical-column change,
        // except for the one narrow, always-lossless INT-to-DECIMAL widening below (only ever
        // needed for a legacy Rating field created before Rating's catalog type became
        // DECIMAL(18,4) — see database/migrations/tenant/045_alter_fieldtype_rating_decimal.sql).
        if (NumericFamilyTypeCodes.Contains(existing.TypeCode) && !string.IsNullOrWhiteSpace(command.Settings))
        {
            NumericSettings? numericSettings = null;
            try
            {
                numericSettings = JsonSerializer.Deserialize<NumericSettings>(
                    command.Settings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { /* already rejected above by _settingsRegistry.Validate */ }

            if (numericSettings?.DisplayAs is string displayAs
                && NumericFamilyTypeCodes.Contains(displayAs)
                && !string.Equals(displayAs, existing.TypeCode, StringComparison.OrdinalIgnoreCase))
            {
                var targetFieldType = await _fieldTypeRepo.GetByCodeAsync(displayAs, ct)
                    ?? throw new NotFoundException("FieldType", displayAs);

                // Bring a legacy INT (pre-migration Rating) column up to DECIMAL(18,4) first —
                // a no-op for every field created after that migration, since Number/Currency/
                // Percent/Rating all already share that physical type.
                await _schemaEngine.WidenIntColumnToDecimalIfNeededAsync(table, existing, ct);

                await _fieldRepo.UpdateFieldTypeAsync(existing.Id, targetFieldType.Id, command.Settings, command.IsRequired, ct);
            }
        }

        // Invariant: a required field with no default value cannot be made required while some role has
        // it set to None — those users would never be able to create a record.
        if (command.IsRequired && string.IsNullOrWhiteSpace(command.DefaultValue))
        {
            var rolesWithNone = await _permRepo.CountRolesWithNoneAccessForFieldAsync(existing.Id, ct);
            if (rolesWithNone > 0)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["DefaultValue"] =
                    [
                        $"'{command.Label}' is required but does not have a default value. Because some users are not " +
                        $"allowed to modify '{command.Label}', those users will not be able to add new records. " +
                        "Supply a default value or uncheck Required."
                    ],
                });
            }
        }

        // ── Unique index ────────────────────────────────────────────────────────
        if (command.IsUnique && !existing.IsUnique)
        {
            // Pre-flight: reject if duplicates already exist.
            if (await _recordRepo.HasDuplicatesAsync(table, existing, ct))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["IsUnique"] = [$"Cannot make '{command.Label}' unique — duplicate values already exist. Remove duplicates first."]
                });
            }
        }

        // ── Encryption lock ─────────────────────────────────────────────────────
        // We only allow toggling encryption (ON or OFF) for an existing field if the table has zero records.
        // Otherwise, existing plaintext/ciphertext data would become unreadable.
        if (existing.IsEncrypted != command.IsEncrypted)
        {
            var recordCount = await _recordRepo.CountAsync(table, Array.Empty<AppField>(), ct: ct);
            if (recordCount > 0)
            {
                var errorMsg = existing.IsEncrypted 
                    ? "A field that has been encrypted cannot be un-encrypted if the table has existing records." 
                    : "Encryption can only be enabled when creating a new field or if the table has no records.";
                
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["IsEncrypted"] = [errorMsg]
                });
            }
        }

        // Save old IsSearchable state before UpdateAsync modifies metadata
        bool wasSearchable = existing.IsSearchable;

        var affected = await _fieldRepo.UpdateAsync(
            existing.PublicId, table.Id,
            command.Label, command.Description,
            command.IsRequired, command.DefaultValue,
            command.IsSearchable, command.IsSortable,
            command.IsFilterable, command.IsReportable, command.IsAuditable,
            command.IsUnique, command.IsEncrypted, command.Settings, ct);

        if (affected == 0)
            throw new NotFoundException("Field", command.FieldPublicId);

        // Unique index: create or drop after the metadata row is committed.
        if (command.IsUnique != existing.IsUnique)
        {
            existing.IsUnique = command.IsUnique;
            await _schemaEngine.SetUniqueAsync(table, existing, command.IsUnique, ct);
        }

        // Backfill: when an optional field becomes required and a default is supplied, fill existing
        // rows whose value is NULL/empty so they remain valid.
        if (command.IsRequired && !string.IsNullOrWhiteSpace(command.DefaultValue) && !existing.IsRequired)
        {
            existing.IsRequired = command.IsRequired;
            existing.DefaultValue = command.DefaultValue;
            await _recordRepo.BackfillDefaultAsync(table, existing, command.DefaultValue!, ct);
        }

        // Search Index Sync: when IsSearchable changes, trigger a backfill or nullify
        if (command.IsSearchable != wasSearchable && existing.Fid.HasValue)
        {
            var isNullify = !command.IsSearchable;
            var docs = await _recordRepo.GetFieldBackfillBatchAsync(_queryContext.TenantId, table.AppId, table.Id, existing.Fid.Value, isNullify, page: 1, pageSize: 500, ct);

            if (docs.Count > 0)
            {
                await _searchService.BulkIndexRecordsAsync(docs, ct);
            }

            var action = command.IsSearchable ? PowerBase.Application.Common.Models.IndexAction.BackfillField : PowerBase.Application.Common.Models.IndexAction.NullifyField;
            var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
            {
                Action = action,
                TenantId = _queryContext.TenantId,
                AppId = table.AppId,
                TableId = table.Id,
                FieldId = existing.Fid.Value,
                Page = 1
            };
            _ = _messagePublisher.PublishAsync(msg, default);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, existing.PublicId.ToString(),
            $"Field modified: {command.Label} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
