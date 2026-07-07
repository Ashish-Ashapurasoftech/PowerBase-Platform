using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IRecordRepository
{
    /// <summary>True if a non-deleted record with the given row Id exists (used to validate Reference values).</summary>
    Task<bool> ExistsAsync(AppTable table, long recordId, CancellationToken ct = default);

    /// <summary>Resolve record PublicIds to their internal row Ids (used by parent-delete restrict).</summary>
    Task<IReadOnlyList<long>> GetIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default);

    /// <summary>Count non-deleted child records whose reference column (f_{referenceFid}) points at the parent row.</summary>
    Task<int> CountReferencingAsync(AppTable childTable, int referenceFid, long parentRecordId, CancellationToken ct = default);

    /// <summary>Search parent records for a Reference picker, returning (row Id, display label) pairs.</summary>
    Task<IReadOnlyList<ReferenceOption>> SearchForReferenceAsync(
        AppTable parentTable, IReadOnlyList<AppField> labelFields, string? search, int take, CancellationToken ct = default);

    /// <summary>Fetch a label value for each of the given parent row Ids (drives Lookup/Reference label resolution).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Aggregate a child field grouped by the reference column, restricted to the given parent Ids
    /// (drives Summary projection). Returns parentId → aggregate value.</summary>
    Task<IReadOnlyDictionary<long, object?>> AggregateByReferenceAsync(
        AppTable childTable, int referenceFid, string function, int? targetFid,
        IReadOnlyCollection<long> parentIds, FilterGroup? filterTree, CancellationToken ct = default);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        FilterGroup? filterTree = null,
        IReadOnlyList<SortSpec>? sortFields = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    Task<int> CountAsync(AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, CancellationToken ct = default);

    Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);

    Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);

    Task DeleteAsync(AppTable table, Guid publicId, CancellationToken ct = default);

    Task BulkDeleteAsync(AppTable table, IReadOnlyList<Guid> publicIds, CancellationToken ct = default);

    /// <summary>Set the given field's column to <paramref name="defaultValue"/> for all non-deleted rows
    /// whose value is currently NULL or empty. Used when an optional field becomes required. Returns rows affected.</summary>
    Task<int> BackfillDefaultAsync(AppTable table, AppField field, string defaultValue, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted rows have a duplicate non-null value in the field's column.</summary>
    Task<bool> HasDuplicatesAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted rows have a non-null, non-empty value in the field's column.</summary>
    Task<bool> HasAnyDataAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Run a GROUP BY aggregation query for Summary reports.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table,
        AppField groupByField,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        string groupByMode = "EqualValues",
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    Task<(IReadOnlyList<string> Values, bool ExceedsLimit)> GetDistinctFieldValuesAsync(
        AppTable table, AppField field, int limit, string? subField = null, CancellationToken ct = default);
}
