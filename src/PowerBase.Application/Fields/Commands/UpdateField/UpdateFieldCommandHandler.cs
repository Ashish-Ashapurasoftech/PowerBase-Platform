using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.UpdateField;

public class UpdateFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;

    public UpdateFieldCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _permRepo = permRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateFieldCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Load the existing field to detect the optional→required transition (for NULL backfill)
        // and to enforce the required+None+default invariant.
        var existing = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
                       ?? throw new NotFoundException("Field", command.FieldPublicId);

        // Invariant: a required field with no default value cannot be made required while some role has
        // it set to None — those users would never be able to create a record.
        if (command.IsRequired && string.IsNullOrWhiteSpace(command.DefaultValue))
        {
            var rolesWithNone = await _permRepo.CountRolesWithNoneAccessForFieldAsync(existing.Id, ct);
            if (rolesWithNone > 0)
            {
                var name = command.Name;
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["DefaultValue"] =
                    [
                        $"'{name}' is required but does not have a default value. Because some users are not " +
                        $"allowed to modify '{name}', those users will not be able to add new records. " +
                        "Supply a default value or uncheck Required."
                    ],
                });
            }
        }

        var affected = await _fieldRepo.UpdateAsync(
            command.FieldPublicId, table.Id,
            command.Name, command.Label, command.Description,
            command.IsRequired, command.DefaultValue,
            command.IsSearchable, command.IsSortable,
            command.IsFilterable, command.IsReportable,
            command.Settings, ct);

        if (affected == 0)
            throw new NotFoundException("Field", command.FieldPublicId);

        // Backfill: when an optional field becomes required and a default is supplied, fill existing
        // rows whose value is NULL/empty so they remain valid.
        if (command.IsRequired && !string.IsNullOrWhiteSpace(command.DefaultValue) && !existing.IsRequired)
        {
            existing.IsRequired = command.IsRequired;
            existing.DefaultValue = command.DefaultValue;
            await _recordRepo.BackfillDefaultAsync(table, existing, command.DefaultValue!, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, command.FieldPublicId.ToString(), $"Field modified: {command.Name} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
