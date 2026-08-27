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

    /// <summary>Same lookup as <see cref="GetIdsByPublicIdsAsync"/>, but keyed by PublicId so callers can
    /// tell which requested ids exist and correlate each row Id back to the record that asked for it
    /// (used by mass update to validate every record before writing anything).</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetIdsByPublicIdsMapAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default);

    /// <summary>Count non-deleted child records whose reference column (f_{referenceFid}) points at the parent row.</summary>
    Task<int> CountReferencingAsync(AppTable childTable, int referenceFid, long parentRecordId, CancellationToken ct = default);

    /// <summary>Search parent records for a Reference picker, returning (row Id, display label) pairs.</summary>
    Task<IReadOnlyList<ReferenceOption>> SearchForReferenceAsync(
        AppTable parentTable, IReadOnlyList<AppField> labelFields, string? search, int take, CancellationToken ct = default);

    /// <summary>Fetch a label value for each of the given parent row Ids (drives Lookup/Reference label resolution).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Row Id → the raw value of an arbitrary column, for the given row Ids. Used to resolve a
    /// table's Set-Key key-field value per row (Lookup/Summary/reference-picker/delete-guard), without
    /// needing SQL-building changes in the existing Id-based methods above.</summary>
    Task<IReadOnlyDictionary<long, object?>> GetColumnValuesByIdsAsync(
        AppTable table, string columnName, IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Reverse lookup: the row Id of the non-deleted row whose given column equals each of the
    /// provided raw values (native-typed — e.g. decimal/DateTime/string — compared directly against the
    /// column with no string cast, so no format-mismatch risk). Used to translate a submitted or stored
    /// Set-Key key value back to a row Id so the existing Id-based repository methods can be reused unchanged.</summary>
    Task<IReadOnlyDictionary<object, long>> GetIdsByColumnValuesAsync(
        AppTable table, string columnName, IReadOnlyCollection<object> values, CancellationToken ct = default);

    /// <summary>Aggregate a child field grouped by the reference column, restricted to the given parent key
    /// values (row Ids for the default Record ID# key, or the parent's key-field raw values for a Set-Key
    /// table — drives Summary projection and the parent-delete restrict check). Returns parentKeyValue →
    /// aggregate value.</summary>
    /// <param name="targetSubField">When the target field is a composite Address field, the JSON
    /// sub-key (see <see cref="PowerBase.Domain.FieldSettings.AddressSubFields"/>) to aggregate
    /// instead of the whole value. Only meaningful with Count/Exists/Min/Max.</param>
    Task<IReadOnlyDictionary<object, object?>> AggregateByReferenceAsync(
        AppTable childTable, int referenceFid, string function, int? targetFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree, string? targetSubField = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        FilterGroup? filterTree = null,
        IReadOnlyList<SortSpec>? sortFields = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    Task<int> CountAsync(AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default);

    /// <summary>Returns true if the table has at least one non-deleted row — an EXISTS check, not a
    /// COUNT, so it stays cheap on tables with millions of records. Used to gate whether a field's
    /// encryption setting can still be changed (see FieldDetailResponse.HasRecords).</summary>
    Task<bool> HasAnyRecordsAsync(AppTable table, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, object?>> GetSearchableFieldsAsync(Guid recordPublicId, CancellationToken ct = default);
    Task<long> GetRecordIdByPublicIdAsync(AppTable table, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, long>> GetRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<long> GetActiveRecordIdByPublicIdAsync(AppTable table, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, long>> GetActiveRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    /// <summary>Writes the same set of field values to every row in <paramref name="recordIds"/> in a
    /// single UPDATE statement (implicitly atomic — either every matched row is updated or none is).
    /// Used by mass update, after constraint validation has already passed for every record.</summary>
    Task<int> MassUpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> recordIds,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    Task DeleteAsync(AppTable table, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    Task BulkDeleteAsync(AppTable table, IReadOnlyList<Guid> publicIds, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    /// <summary>Set the given field's column to <paramref name="defaultValue"/> for all non-deleted rows
    /// whose value is currently NULL or empty. Used when an optional field becomes required. Returns rows affected.</summary>
    Task<int> BackfillDefaultAsync(AppTable table, AppField field, string defaultValue, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted rows have a duplicate non-null value in the field's column.</summary>
    Task<bool> HasDuplicatesAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Returns true if any other non-deleted row already has <paramref name="value"/> in the
    /// field's column — the per-write check behind the Unique constraint. <paramref name="excludeRecordId"/>
    /// (the record's internal row Id, not PublicId) excludes the record being updated from the check.</summary>
    Task<bool> HasValueDuplicateAsync(AppTable table, AppField field, object value, long? excludeRecordId = null, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted row has a NULL or empty-string value in the field's
    /// column (a candidate key field must be populated on every row).</summary>
    Task<bool> HasNullsAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Set Key cascade rewire: for every non-deleted child row whose <paramref name="oldColumn"/>
    /// raw value (native-typed — row Id, or a Set-Key key value of any scalar type) matches a key in
    /// <paramref name="oldToNewValue"/>, write the mapped value into <paramref name="newColumn"/>.
    /// Bounded by distinct parent count (chunked), not child row count.</summary>
    Task RewriteReferenceColumnAsync(
        AppTable childTable, string oldColumn, string newColumn,
        IReadOnlyDictionary<object, object?> oldToNewValue, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted rows have a non-null, non-empty value in the field's column.</summary>
    Task<bool> HasAnyDataAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Run a GROUP BY aggregation query for Summary (and Chart) reports. When <paramref name="seriesField"/>
    /// is supplied (Chart reports only), groups by both <paramref name="groupByField"/> and it, and each result
    /// row additionally carries a "SeriesValue" key.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table,
        AppField groupByField,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        string groupByMode = "EqualValues",
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        AppField? seriesField = null,
        string seriesMode = "EqualValues",
        CancellationToken ct = default);

    Task<(IReadOnlyList<string> Values, bool ExceedsLimit)> GetDistinctFieldValuesAsync(
        AppTable table, AppField field, int limit, string? subField = null, CancellationToken ct = default);

    Task<int> SanitizeTableEncryptedDataAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default);
    Task<IReadOnlyList<SearchIndexDocument>> GetFieldBackfillBatchAsync(long tenantId, long appId, long tableId, long fieldId, bool isNullify, int page, int pageSize, CancellationToken ct = default);
}
