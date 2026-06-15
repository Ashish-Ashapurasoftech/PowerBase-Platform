using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
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

    public GetDistinctFieldValuesQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
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

        var (values, exceedsLimit) = await _recordRepo.GetDistinctFieldValuesAsync(table, field, query.Limit, query.SubField, ct);

        return new DistinctValuesResponse(values, exceedsLimit);
    }
}
