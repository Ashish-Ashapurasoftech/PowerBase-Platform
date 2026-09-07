using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports.Queries.RunReport;
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

        // Level 1: ViewScope = None → this user cannot see any records in this table.
        if (!access.CanView)
            throw new NotFoundException("Record", query.RecordPublicId);

        // ViewFilter conditions on Formula/Lookup/Summary fields have no physical column, so the
        // SQL EXISTS check below silently drops them (same as CountAsync/ListAsync). Split them
        // out and evaluate in-memory against the projected record — mirroring how
        // RunReportQueryHandler applies formula-field conditions — so a role ViewFilter based on
        // a computed field is actually enforced on direct record-ID access, not bypassed.
        var formulaFids = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();
        var (physicalViewFilter, formulaViewFilter) = FormulaFilterSorter.SplitFilterTree(access.ViewFilter, formulaFids);

        // Level 2 & 3: Enforce OwnRecords (ViewScope) AND the physical part of the role ViewFilter.
        // Both are evaluated together in a single SQL EXISTS query so a restricted record
        // is indistinguishable from a non-existent one — the security principle that a
        // record the user cannot see does not exist for them.
        if (!access.Unrestricted && (physicalViewFilter != null || access.RestrictToCreatedBy != null))
        {
            var isVisible = await _recordRepo.ExistsWithViewFilterAsync(
                table,
                fields,
                query.RecordPublicId,
                viewFilter: physicalViewFilter,
                restrictToCreatedBy: access.RestrictToCreatedBy,
                ct);

            if (!isVisible)
                throw new NotFoundException("Record", query.RecordPublicId);
        }

        var visibleFields = access.VisibleFields;
        var row = await _recordRepo.GetByPublicIdAsync(table, visibleFields, query.RecordPublicId, ct);
        var userNames = await RunReportQueryHandler.ResolveUserNamesAsync([row], visibleFields, _userRepo, ct);
        var relational = await _relationalProjector.ProjectAsync(table, visibleFields, [row], ct);
        var computed = _formulaProjector.Project(visibleFields, [row], relational, table);

        // Level 4: Enforce the formula/lookup part of the ViewFilter now that computed values exist.
        if (!access.Unrestricted && formulaViewFilter != null)
        {
            var matches = FormulaFilterSorter.ApplyFormulaFilters([(row, computed[0])], formulaViewFilter, fields);
            if (matches.Count == 0)
                throw new NotFoundException("Record", query.RecordPublicId);
        }

        return Records.RecordResult.FromRow(row, visibleFields, userNames, computed[0]);
    }
}
