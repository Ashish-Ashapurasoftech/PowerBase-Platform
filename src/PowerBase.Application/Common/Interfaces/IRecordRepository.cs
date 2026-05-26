using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IRecordRepository
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        IReadOnlyList<ReportFilter>? filters = null,
        long? sortFieldId = null, bool sortDesc = false,
        CancellationToken ct = default);

    Task<int> CountAsync(AppTable table, IReadOnlyList<ReportFilter>? filters = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, CancellationToken ct = default);

    Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);

    Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);

    Task DeleteAsync(AppTable table, Guid publicId, CancellationToken ct = default);

    /// <summary>Run a GROUP BY aggregation query for Summary reports.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table,
        AppField groupByField,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        string groupByMode = "EqualValues",
        CancellationToken ct = default);
}
