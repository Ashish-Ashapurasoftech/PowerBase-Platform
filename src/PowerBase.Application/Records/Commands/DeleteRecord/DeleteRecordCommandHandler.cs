using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Records.Commands.DeleteRecord;

public class DeleteRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IRecordRepository _recordRepo;

    public DeleteRecordCommandHandler(IAppTableRepository tableRepo, IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _recordRepo = recordRepo;
    }

    public async Task HandleAsync(DeleteRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        await _recordRepo.DeleteAsync(table, command.RecordPublicId, ct);
    }
}
