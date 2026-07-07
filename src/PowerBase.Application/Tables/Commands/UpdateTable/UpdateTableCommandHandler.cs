using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.UpdateTable;

public class UpdateTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    public UpdateTableCommandHandler(IAppTableRepository tableRepo, IAppAccessService appAccessService, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateTableCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        if (table == null)
            throw new NotFoundException("Table", command.TablePublicId);

        var changes = new List<string>();
        if (table.Name != command.Name)
            changes.Add($"Name to '{command.Name}'");
        if (table.SingularLabel != command.SingularLabel)
            changes.Add($"Singular Label to '{command.SingularLabel}'");
        if (table.PluralLabel != command.PluralLabel)
            changes.Add($"Plural Label to '{command.PluralLabel}'");
        if (table.Description != command.Description)
            changes.Add("Description");
        if (table.Icon != command.Icon)
            changes.Add($"Icon to '{command.Icon}'");
        if (table.DefaultRecordPickerField1Id != command.DefaultRecordPickerField1Id)
            changes.Add("Default Record Picker Field 1");
        if (table.DefaultRecordPickerField2Id != command.DefaultRecordPickerField2Id)
            changes.Add("Default Record Picker Field 2");
        if (table.DefaultRecordPickerField3Id != command.DefaultRecordPickerField3Id)
            changes.Add("Default Record Picker Field 3");

        var affected = await _tableRepo.UpdateAsync(
            command.TablePublicId, command.Name,
            command.SingularLabel, command.PluralLabel,
            command.Description, command.Icon, 
            command.DefaultRecordPickerField1Id,
            command.DefaultRecordPickerField2Id,
            command.DefaultRecordPickerField3Id,
            ct);

        if (affected > 0 && changes.Count > 0)
        {
            var logMessage = $"Table updated: {string.Join(", ", changes)}";
            await _auditRepo.LogActivityAsync(
                AuditActions.Updated, AuditEntityTypes.AppTable, command.TablePublicId.ToString(), logMessage, appId: table.AppId, ct: ct);
        }
    }
}
