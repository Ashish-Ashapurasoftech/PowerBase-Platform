using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Fields.Commands.UpdateField;

public class UpdateFieldCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public UpdateFieldCommandHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task HandleAsync(UpdateFieldCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var field = await _fieldRepo.GetByIdInTableAsync(command.FieldId, table.Id, ct);

        field.Label = command.Label;
        field.Description = command.Description;
        field.IsRequired = command.IsRequired;

        await _fieldRepo.UpdateAsync(field, ct);
    }
}
