using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.UpdateTable;

public class UpdateTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateTableCommandHandler(IAppTableRepository tableRepo, IAppAccessService appAccessService)
    {
        _tableRepo = tableRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateTableCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var affected = await _tableRepo.UpdateAsync(
            command.TablePublicId, command.Name,
            command.SingularLabel, command.PluralLabel,
            command.Description, command.Icon, ct);

        if (affected == 0)
            throw new NotFoundException("Table", command.TablePublicId);
    }
}
