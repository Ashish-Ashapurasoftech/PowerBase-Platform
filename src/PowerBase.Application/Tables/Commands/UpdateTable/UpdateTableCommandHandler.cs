using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.UpdateTable;

public class UpdateTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;

    public UpdateTableCommandHandler(IAppTableRepository tableRepo)
    {
        _tableRepo = tableRepo;
    }

    public async Task HandleAsync(UpdateTableCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.PublicId, ct);
        table.Name = command.Name;
        table.SingularLabel = command.SingularLabel;
        table.PluralLabel = command.PluralLabel;
        table.Description = command.Description;
        table.Icon = command.Icon;

        await _tableRepo.UpdateAsync(table, ct);
    }
}
