using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;

namespace PowerBase.Application.Fields.Queries.SharedValues;

/// <summary>A field flagged "Shared values", offered as a choice source ("From another field") — in any app the user belongs to.</summary>
public class SharedFieldDto
{
    public Guid AppPublicId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public Guid TablePublicId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public Guid FieldPublicId { get; set; }
    public int Fid { get; set; }
    public string? Label { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeCode { get; set; } = string.Empty;
}

public record SharedValuesResponse(IReadOnlyList<string> Values, bool ExceedsLimit);

/// <summary>Lists the Shared-values fields of every app the current user belongs to.</summary>
public class ListSharedFieldsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IQueryContext _queryContext;

    public ListSharedFieldsQueryHandler(IAppRepository appRepo, IAppFieldRepository fieldRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _fieldRepo = fieldRepo;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<SharedFieldDto>> HandleAsync(CancellationToken ct = default)
    {
        var apps = await _appRepo.ListAllByUserAsync(_queryContext.UserId, ct);
        if (apps.Count == 0) return Array.Empty<SharedFieldDto>();
        return await _fieldRepo.ListSharedValuesFieldsAsync(apps.Select(a => a.Id).ToList(), ct);
    }
}

/// <summary>
/// The current distinct values of a Shared-values field, for another field's dropdown. Read fresh on every call, so the
/// choices follow the source data. The field's owner opted in by turning Shared values on, so — unlike the Reports filter
/// dropdown — the values are not limited by the caller's role in the source app.
/// </summary>
public class GetSharedFieldValuesQueryHandler
{
    public const int MaxValues = 1000;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public GetSharedFieldValuesQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<SharedValuesResponse> HandleAsync(Guid tableId, int fieldFid, int limit, CancellationToken ct = default)
    {
        var empty = new SharedValuesResponse(Array.Empty<string>(), false);
        var table = await _tableRepo.GetByPublicIdAsync(tableId, ct);
        if (table is null || table.IsDeleted) return empty;

        var field = (await _fieldRepo.ListByTableAsync(table.Id, ct)).FirstOrDefault(f => f.Fid == fieldFid && !f.IsDeleted);
        if (field is null || !field.IsSharedValues) return empty;
        if (FormulaTypeMap.IsComputedField(field.TypeCode, field.Settings)) return empty;

        var (values, exceedsLimit) = await _recordRepo.GetDistinctFieldValuesAsync(
            table, field, Math.Clamp(limit, 1, MaxValues), subField: null, ct, onlyRowIds: null, dropUndecryptable: true);
        return new SharedValuesResponse(values, exceedsLimit);
    }
}
