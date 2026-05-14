using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Tables.Commands.DeleteTable;

public class DeleteTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;

    public DeleteTableCommandHandler(IAppTableRepository tableRepo)
    {
        _tableRepo = tableRepo;
    }

    public async Task HandleAsync(DeleteTableCommand command, CancellationToken ct = default)
    {
        await _tableRepo.DeleteAsync(command.PublicId, ct);
    }
}
