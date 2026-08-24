using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.UpdateField;

public class UpdateFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly FieldSettingsValidatorRegistry _settingsRegistry;

    public UpdateFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo,
        ISchemaEngineService schemaEngine,
        FieldSettingsValidatorRegistry settingsRegistry)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _permRepo = permRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        _schemaEngine = schemaEngine;
        _settingsRegistry = settingsRegistry;
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

        // Reject Required/Unique/DefaultValue combinations the field's type doesn't support (only when
        // something is being turned ON — leaving a flag off/blank is always allowed).
        var capErrors = FieldGeneralSettingsCapability.Validate(
            existing.TypeCode, command.Settings ?? existing.Settings, command.Label,
            command.IsRequired, command.IsUnique, command.DefaultValue);
        if (capErrors.Count > 0)
            throw new ValidationException(capErrors.AsReadOnly());

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

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, existing.PublicId.ToString(),
            $"Field modified: {command.Label} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
