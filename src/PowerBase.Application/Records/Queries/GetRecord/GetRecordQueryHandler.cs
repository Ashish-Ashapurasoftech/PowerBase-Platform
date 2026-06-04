using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Queries.GetRecord;

public class GetRecordQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public GetRecordQueryHandler(
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

    public async Task<Records.RecordResult> HandleAsync(GetRecordQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView)
            throw new NotFoundException("Record", query.RecordPublicId);
        if (!access.Unrestricted && access.ViewScope == RecordScopes.OwnRecords)
            await _enforcer.EnsureRecordOwnedAsync(table, query.RecordPublicId, ct);

        var visibleFields = access.VisibleFields;
        var row = await _recordRepo.GetByPublicIdAsync(table, visibleFields, query.RecordPublicId, ct);
        return Records.RecordResult.FromRow(row, visibleFields);
    }
}
