using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using System.Linq;

namespace PowerBase.Application.Records.Queries.GetDistinctFieldValues;

public record DistinctValuesResponse(IReadOnlyList<string> Values, bool ExceedsLimit);

public record GetDistinctFieldValuesQuery(Guid TableId, int FieldFid, int Limit = 25, string? SubField = null);

public class GetDistinctFieldValuesQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IUserRepository _userRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IRelationalProjector _relationalProjector;
    private readonly IFormulaProjector _formulaProjector;

    public GetDistinctFieldValuesQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IUserRepository userRepo,
        IRolePermissionEnforcer enforcer,
        IRelationalProjector relationalProjector,
        IFormulaProjector formulaProjector)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _userRepo = userRepo;
        _enforcer = enforcer;
        _relationalProjector = relationalProjector;
        _formulaProjector = formulaProjector;
    }

    public async Task<DistinctValuesResponse> HandleAsync(GetDistinctFieldValuesQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TableId, ct);
        if (table == null) return new DistinctValuesResponse(new List<string>(), false);

        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var field = fields.FirstOrDefault(f => f.Fid == query.FieldFid);
        if (field == null) return new DistinctValuesResponse(new List<string>(), false);

        // Formula fields have no physical column — return empty (frontend falls back to text input)
        if (FormulaTypeMap.IsComputedField(field.TypeCode, field.Settings))
            return new DistinctValuesResponse(new List<string>(), true);

        // Only values of records the role may view, and only for a field it may see — otherwise the dropdown
        // would reveal values from records hidden by its Custom Rules / Own records scope.
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView || !access.VisibleFields.Any(f => f.Id == field.Id))
            return new DistinctValuesResponse(new List<string>(), false);
        var onlyRowIds = await Records.ViewableRowIds.ResolveAsync(
            access, table, fields, _recordRepo, _relationalProjector, _formulaProjector, ct);
        if (onlyRowIds is { Count: 0 }) return new DistinctValuesResponse(new List<string>(), false);

        var (values, exceedsLimit) = await _recordRepo.GetDistinctFieldValuesAsync(
            table, field, query.Limit, query.SubField, ct, onlyRowIds);

        // User/MultiUser (incl. the built-in Record Owner/Last Modified By system fields) store
        // a plain core.[User].Id (BIGINT) in the tenant column — RecordRepository intentionally
        // returns those raw, since the user directory itself lives in the CONTROL database it
        // has no connection to. Resolve names here via IUserRepository (the same one
        // RunReportQueryHandler.ResolveUserNamesAsync uses for the main record grid), and
        // re-pack each option as "{id}|{name}" — same composite-pair convention the Phone branch
        // inside GetDistinctFieldValuesAsync already uses, so the frontend's existing split logic
        // (table-report-view.component.ts's splitDistinctOption) picks it up unchanged.
        if ((field.TypeCode == "User" || field.TypeCode == "MultiUser") && values.Count > 0)
        {
            var ids = values
                .Select(v => long.TryParse(v, out var id) ? id : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
            var names = await _userRepo.GetNamesByIdsAsync(ids, ct);
            values = values
                .Select(v => long.TryParse(v, out var id) && names.TryGetValue(id, out var name) ? $"{v}|{name}" : v)
                .ToList();
        }

        return new DistinctValuesResponse(values, exceedsLimit);
    }
}
