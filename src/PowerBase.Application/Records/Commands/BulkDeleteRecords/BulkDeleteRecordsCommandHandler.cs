using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.BulkDeleteRecords;

public class BulkDeleteRecordsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IRecordRepository _recordRepo;

    public BulkDeleteRecordsCommandHandler(IAppTableRepository tableRepo, IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _recordRepo = recordRepo;
    }

    public async Task HandleAsync(BulkDeleteRecordsCommand command, CancellationToken ct = default)
    {
        if (command.RecordPublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["At least one record ID is required."] });
        if (command.RecordPublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["ids"] = ["Cannot delete more than 500 records at once."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        await _recordRepo.BulkDeleteAsync(table, command.RecordPublicIds, ct);
    }
}
