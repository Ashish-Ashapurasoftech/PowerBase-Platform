using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records;

/// <summary>
/// Enforces the role's Custom Rules on writes: a record may be edited or deleted only when it matches both the
/// View rules (<see cref="TableAccessContext.ViewFilter"/> — a record the user cannot see cannot be changed) and the
/// Modify rules (<see cref="TableAccessContext.ModifyFilter"/>), even when its id is known. Conditions on physical fields
/// are checked in SQL; conditions on Formula/Lookup/Summary fields have no column, so they are evaluated in
/// memory against the projected record — the same split GetRecord uses for the View rules.
/// </summary>
public class RecordModifyGuard
{
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationalProjector _relationalProjector;
    private readonly IFormulaProjector _formulaProjector;

    public RecordModifyGuard(
        IRecordRepository recordRepo, IRelationalProjector relationalProjector, IFormulaProjector formulaProjector)
    {
        _recordRepo = recordRepo;
        _relationalProjector = relationalProjector;
        _formulaProjector = formulaProjector;
    }

    /// <summary>Throws when any of the records may not be modified by the current user.</summary>
    public async Task EnsureAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields,
        IEnumerable<Guid> recordPublicIds, CancellationToken ct = default)
    {
        var filter = WriteFilter(access);
        if (filter is null) return;
        var split = Split(filter, fields);
        foreach (var id in recordPublicIds.Distinct())
            if (!await IsAllowedAsync(split, table, fields, id, ct))
                throw new UnauthorizedActionException("modify this record");
    }

    /// <summary>Throws when the record does not match the View rules (button actions, which may also be
    /// offered to users without edit permission, so only visibility is required).</summary>
    public async Task EnsureVisibleAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields, Guid recordPublicId,
        CancellationToken ct = default)
    {
        if (access.Unrestricted || access.ViewFilter is null) return;
        if (!await IsAllowedAsync(Split(access.ViewFilter, fields), table, fields, recordPublicId, ct))
            throw new UnauthorizedActionException("view this record");
    }

    /// <summary>The records the current user may modify (used by background deletes that skip the rest).</summary>
    public async Task<List<Guid>> FilterAllowedAsync(
        TableAccessContext access, AppTable table, IReadOnlyList<AppField> fields,
        IEnumerable<Guid> recordPublicIds, CancellationToken ct = default)
    {
        var ids = recordPublicIds.Distinct().ToList();
        var filter = WriteFilter(access);
        if (filter is null) return ids;
        var split = Split(filter, fields);
        var allowed = new List<Guid>(ids.Count);
        foreach (var id in ids)
            if (await IsAllowedAsync(split, table, fields, id, ct)) allowed.Add(id);
        return allowed;
    }

    /// <summary>View rules AND Modify rules; null when neither restricts (or the user is unrestricted).</summary>
    private static FilterGroup? WriteFilter(TableAccessContext access)
    {
        if (access.Unrestricted) return null;
        if (access.ViewFilter is null) return access.ModifyFilter;
        if (access.ModifyFilter is null) return access.ViewFilter;
        return new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Group = access.ViewFilter }, new FilterNode { Group = access.ModifyFilter }],
        };
    }

    private static (FilterGroup? Physical, FilterGroup? Formula) Split(FilterGroup filter, IReadOnlyList<AppField> fields)
    {
        var formulaFids = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();
        return FormulaFilterSorter.SplitFilterTree(filter, formulaFids);
    }

    private async Task<bool> IsAllowedAsync(
        (FilterGroup? Physical, FilterGroup? Formula) split, AppTable table, IReadOnlyList<AppField> fields,
        Guid id, CancellationToken ct)
    {
        if (split.Physical is not null
            && !await _recordRepo.ExistsWithViewFilterAsync(table, fields, id, split.Physical, null, ct))
            return false;

        if (split.Formula is null) return true;

        var row = await _recordRepo.GetByPublicIdAsync(table, fields, id, ct: ct);
        var relational = await _relationalProjector.ProjectAsync(table, fields, [row], ct);
        var computed = _formulaProjector.Project(fields, [row], relational, table);
        return FormulaFilterSorter.ApplyFormulaFilters([(row, computed[0])], split.Formula, fields).Count > 0;
    }
}
