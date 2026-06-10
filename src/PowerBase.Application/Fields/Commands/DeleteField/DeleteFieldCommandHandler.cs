using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.DeleteField;

public class DeleteFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteFieldCommandHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteFieldCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var field = await _fieldRepo.GetByFidInTableAsync(table.Id, command.FieldFid, ct)
            ?? throw new NotFoundException("Field", command.FieldFid);

        if (field.IsSystem)
            throw new UnauthorizedActionException("System fields cannot be deleted.");

        var affected = await _fieldRepo.DeleteAsync(field.PublicId, table.Id, ct);
        if (affected == 0)
            throw new NotFoundException("Field", command.FieldFid);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(), $"Field deleted: {field.Name} From TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
