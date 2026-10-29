using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Queries.ListArchivedRecords;

public class ListArchivedRecordsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public ListArchivedRecordsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    public async Task<ArchivedRecordsResult> HandleAsync(ListArchivedRecordsQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted && !access.CanView && !access.CanDelete)
        {
            throw new UnauthorizedActionException("You do not have permission to view archived records in this table.");
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 50 : query.PageSize;

        var items = await _recordRepo.ListArchivedAsync(table, fields, page, pageSize, ct);
        var total = await _recordRepo.CountArchivedAsync(table, ct);

        return new ArchivedRecordsResult(items, total, page, pageSize);
    }
}
