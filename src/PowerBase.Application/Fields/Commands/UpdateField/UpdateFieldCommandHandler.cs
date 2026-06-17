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
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Load the existing field to:
        //   (a) get its TypeCode for Settings validation,
        //   (b) detect the optional→required transition (for NULL backfill),
        //   (c) enforce the required+None+default invariant.
        var existing = await _fieldRepo.GetByFidInTableAsync(table.Id, command.FieldFid, ct)
                       ?? throw new NotFoundException("Field", command.FieldFid);

        // Validate per-type Settings JSON against the field's current type.
        var settingsErrors = _settingsRegistry.Validate(existing.TypeCode, command.Settings);
        if (settingsErrors.Count > 0)
            throw new ValidationException(settingsErrors.AsReadOnly());

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
                        $"'{command.Name}' is required but does not have a default value. Because some users are not " +
                        $"allowed to modify '{command.Name}', those users will not be able to add new records. " +
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
                    ["IsUnique"] = [$"Cannot make '{command.Name}' unique — duplicate values already exist. Remove duplicates first."]
                });
            }
        }

        var affected = await _fieldRepo.UpdateAsync(
            existing.PublicId, table.Id,
            command.Name, command.Label, command.Description,
            command.IsRequired, command.DefaultValue,
            command.IsSearchable, command.IsSortable,
            command.IsFilterable, command.IsReportable, command.IsAuditable,
            command.IsUnique, command.Settings, ct);

        if (affected == 0)
            throw new NotFoundException("Field", command.FieldFid);

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
            $"Field modified: {command.Name} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
