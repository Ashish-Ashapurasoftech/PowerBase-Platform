using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Records.Queries.GetRecord;

public class GetRecordQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public GetRecordQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<Records.RecordResult> HandleAsync(GetRecordQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var row = await _recordRepo.GetByPublicIdAsync(table, fields, query.RecordPublicId, ct);
        return Records.RecordResult.FromRow(row, fields);
    }
}
