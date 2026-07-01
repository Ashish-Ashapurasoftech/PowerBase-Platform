using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Queries.GetRecord;

public class GetRecordQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IUserRepository _userRepo;
    private readonly IFormulaProjector _formulaProjector;
    private readonly IRelationalProjector _relationalProjector;

    public GetRecordQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector,
        IRelationalProjector relationalProjector)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _userRepo = userRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
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
        var userNames = await RunReportQueryHandler.ResolveUserNamesAsync([row], visibleFields, _userRepo, ct);
        var relational = await _relationalProjector.ProjectAsync(table, visibleFields, [row], ct);
        var computed = _formulaProjector.Project(visibleFields, [row], relational, table);
        return Records.RecordResult.FromRow(row, visibleFields, userNames, computed[0]);
    }
}
