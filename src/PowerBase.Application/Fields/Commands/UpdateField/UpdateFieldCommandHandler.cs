using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Settings;
using PowerBase.Application.Fields.Versioning;
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
        IAzureSearchService searchService)
    {
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
        var label = existing.IsSystem ? existing.Label! : command.Label;
        var description = existing.IsSystem ? existing.Description : command.Description;
        var isRequired = existing.IsSystem ? false : command.IsRequired;
        var defaultValue = existing.IsSystem ? null : command.DefaultValue;
        var isUnique = existing.IsSystem ? false : command.IsUnique;
        var isSortable = existing.IsSystem ? false : command.IsSortable;
        var isFilterable = existing.IsSystem ? false : command.IsFilterable;
        var isAuditable = existing.IsSystem ? false : command.IsAuditable;
        var isEncrypted = existing.IsSystem ? false : command.IsEncrypted;
        var settings = existing.IsSystem
            ? SystemFieldSettingsPolicy.RestrictSettingsJson(existing.TypeCode, command.Settings)
            : command.Settings;

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, excludeFieldId: existing.Id, ct: ct))
            throw new DuplicateException("Field", "label", label);

        _guard.ValidateSettingsAndCapabilities(
            existing.TypeCode, settings, settings ?? existing.Settings, label, isRequired, isUnique, defaultValue);

        // ── Numeric family "Display As" type switch ─────────────────────────────
        // Number/Currency/Percent/Rating share one settings shape (NumericSettings) with a
        // DisplayAs member. When it names a different TypeCode within that same family, the
        // field's actual type changes to match — e.g. a Percent field whose Display As is
        // switched to Currency becomes a Currency field. TypeCode is a pure FieldTypeId swap
        // (see AppFieldRepository.UpdateFieldTypeAsync) — never a physical-column change,
        // except for the one narrow, always-lossless INT-to-DECIMAL widening below (only ever
        // needed for a legacy Rating field created before Rating's catalog type became
        // DECIMAL(18,4) — see database/migrations/tenant/045_alter_fieldtype_rating_decimal.sql).
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

                // Bring a legacy INT (pre-migration Rating) column up to DECIMAL(18,4) first —
                // a no-op for every field created after that migration, since Number/Currency/
                // Percent/Rating all already share that physical type.
                await _schemaEngine.WidenIntColumnToDecimalIfNeededAsync(table, existing, ct);

                await _fieldRepo.UpdateFieldTypeAsync(existing.Id, targetFieldType.Id, settings, isRequired, ct);
            }
        }

        await _guard.ValidateRequiredHasDefaultOrNoRestrictedRolesAsync(existing.Id, label, isRequired, defaultValue, ct);
        await _guard.ValidateUniqueTransitionAsync(table, existing, label, isUnique, ct);
        await _guard.ValidateEncryptionTransitionAsync(table, existing, isEncrypted, ct);

        // Save old IsSearchable state before UpdateAsync modifies metadata
        bool wasSearchable = existing.IsSearchable;

        var after = new FieldSnapshot(
            label, description, isRequired, defaultValue, command.IsSearchable, isSortable,
            isFilterable, command.IsReportable, isAuditable, isUnique, isEncrypted, settings);

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
                isUnique, isEncrypted, settings, ct, _uow.Transaction);

            if (affected == 0)
                throw new NotFoundException("Field", command.FieldPublicId);

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

        // Unique index: create or drop after the metadata row is committed.
        if (isUnique != existing.IsUnique)
        {
            existing.IsUnique = isUnique;
            await _schemaEngine.SetUniqueAsync(table, existing, isUnique, ct);
        }

        // Backfill: when an optional field becomes required and a default is supplied, fill existing
        // rows whose value is NULL/empty so they remain valid.
        if (isRequired && !string.IsNullOrWhiteSpace(defaultValue) && !existing.IsRequired)
        {
            existing.IsRequired = isRequired;
            existing.DefaultValue = defaultValue;
            await _recordRepo.BackfillDefaultAsync(table, existing, defaultValue!, ct);
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
            $"Field modified: {label} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
