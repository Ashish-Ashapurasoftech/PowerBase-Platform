using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

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
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var affected = await _fieldRepo.UpdateAsync(
            command.FieldPublicId, table.Id,
            command.Name, command.Label, command.Description,
            command.IsRequired, command.DefaultValue,
            command.IsSearchable, command.IsSortable,
            command.IsFilterable, command.IsReportable,
            command.Settings, ct);

        if (affected == 0)
            throw new NotFoundException("Field", command.FieldPublicId);
    }
}
