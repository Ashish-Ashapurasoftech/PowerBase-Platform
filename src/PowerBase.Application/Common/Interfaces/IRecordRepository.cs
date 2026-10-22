using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IRecordRepository
{
    /// <summary>True if a non-deleted record with the given row Id exists (used to validate Reference values).</summary>
    Task<bool> ExistsAsync(AppTable table, long recordId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the non-deleted record identified by <paramref name="publicId"/> exists AND passes
    /// all role-level record-level permission constraints (<paramref name="viewFilter"/> AND/OR
    /// <paramref name="restrictToCreatedBy"/>). Used by <c>GetRecordQueryHandler</c> to enforce
    /// ViewFilter on direct record-ID access so a restricted record cannot be fetched by knowing its URL.
    /// </summary>
    Task<bool> ExistsWithViewFilterAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid publicId,
        FilterGroup? viewFilter,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    /// <summary>Resolve record PublicIds to their internal row Ids (used by parent-delete restrict).</summary>
    Task<IReadOnlyList<long>> GetIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default);

    /// <summary>Same lookup as <see cref="GetIdsByPublicIdsAsync"/>, but keyed by PublicId so callers can
    /// tell which requested ids exist and correlate each row Id back to the record that asked for it
    /// (used by mass update to validate every record before writing anything).</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetIdsByPublicIdsMapAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default);

    /// <summary>Count non-deleted child records whose reference column (f_{referenceFid}) points at the parent row.</summary>
    Task<int> CountReferencingAsync(AppTable childTable, int referenceFid, long parentRecordId, CancellationToken ct = default);

    /// <summary>Search parent records for a Reference picker, returning (row Id, display label) pairs.
    /// <paramref name="primaryLabelField"/> is the parent's descriptive/picker field to project as
    /// <see cref="ReferenceOption.Label"/> for the closed-input text; pass null to fall back to
    /// <paramref name="labelFields"/>[0] (the standard-key case, where they're the same field).</summary>
    Task<IReadOnlyList<ReferenceOption>> SearchForReferenceAsync(
        AppTable parentTable, IReadOnlyList<AppField> labelFields, string? search, int take,
        AppField? primaryLabelField = null, CancellationToken ct = default,
        IReadOnlyList<ReferenceFilterClause>? filters = null,
        IReadOnlyCollection<long>? onlyRowIds = null);

    /// <summary>True when the parent row satisfies every dependent-dropdown clause (used to reject a
    /// Reference value the picker would not have offered).</summary>
    Task<bool> MatchesReferenceFilterAsync(
        AppTable parentTable, long parentRowId, IReadOnlyList<ReferenceFilterClause> filters, CancellationToken ct = default);

    /// <summary>Fetch a label value for each of the given parent row Ids (drives Lookup/Reference label resolution).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>Bulk-upsert lookups that run inside the commit transaction.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetBulkUpsertRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, System.Data.IDbTransaction transaction, CancellationToken ct = default);

    Task<IReadOnlyDictionary<object, IReadOnlyDictionary<string, object?>>> GetBulkUpsertRowsByColumnValuesAsync(
        AppTable table, IReadOnlyList<AppField> fields, string columnName, IReadOnlyCollection<object> values, System.Data.IDbTransaction transaction, CancellationToken ct = default);

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
    /// instead of the whole value. Only meaningful with Count/Exists/DistinctCount.</param>
    /// <param name="fieldLookup">The child table's fields by Fid, so <paramref name="filterTree"/>
    /// is built with each field's type — the same way a report's filter is.</param>
    /// <param name="parentScope">The parent table, so "parentField" conditions in
    /// <paramref name="filterTree"/> can compare each child with its own parent's field. Without
    /// it those conditions are no-ops.</param>
    Task<IReadOnlyDictionary<object, object?>> AggregateByReferenceAsync(
        AppTable childTable, int referenceFid, string function, int? targetFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree, string? targetSubField = null,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null,
        CancellationToken ct = default);

    /// <summary>The target field's raw non-NULL values for every child of the given parents, in
    /// display order — grouped by parent, then by <paramref name="sortFid"/> (or record creation
    /// order when null), then Id. Feeds the Combined Text summary, which formats each value with its
    /// field's display format before joining (hence raw rows, not a SQL STRING_AGG).
    /// <paramref name="fieldLookup"/> and <paramref name="parentScope"/> are as for
    /// <see cref="AggregateByReferenceAsync"/>.</summary>
    Task<IReadOnlyList<(object ParentKey, object Value)>> ListValuesByReferenceAsync(
        AppTable childTable, int referenceFid, int targetFid, string? targetSubField,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree,
        int? sortFid, bool sortDescending, IReadOnlyDictionary<long, AppField>? fieldLookup = null,
        ParentFieldScope? parentScope = null, CancellationToken ct = default);

    /// <summary>Every child row (stored columns of <paramref name="fields"/>, plus Id) of the given
    /// parents that matches <paramref name="filterTree"/>, ordered by Id. Feeds a summary over a
    /// Formula field, whose value has no column to aggregate in SQL and is computed per row instead.
    /// <paramref name="fieldLookup"/> and <paramref name="parentScope"/> are as for
    /// <see cref="AggregateByReferenceAsync"/>.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListRowsByReferenceAsync(
        AppTable childTable, IReadOnlyList<AppField> fields, int referenceFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null,
        CancellationToken ct = default);

    /// <summary>Every live row of the table (stored columns of <paramref name="fields"/>, plus Id and
    /// the system columns), decrypted, ordered by Id. Feeds a summary over encrypted data, which SQL
    /// can't match, group or aggregate and so is computed in memory.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAllRowsDecryptedAsync(
        AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default);

    /// <summary>Encrypts <paramref name="plainByRowId"/> (row Id → text) the way a stored value of
    /// <paramref name="field"/> is encrypted on write — unchanged when that field's column isn't encrypted (app and
    /// field both unencrypted, or an encryption-exempt type). Used to refill a column with ciphertext in bulk.</summary>
    Task<IReadOnlyDictionary<long, string>> EncryptColumnTextsAsync(
        AppTable table, AppField field, IReadOnlyDictionary<long, string> plainByRowId, CancellationToken ct = default);

    /// <summary>The live rows of <paramref name="child"/> whose <paramref name="reference"/> is blank and whose hidden
    /// <paramref name="original"/> field kept the text that matched no parent record (see <c>ReferenceSettings.OriginalFid</c>),
    /// with that text decrypted. The blank references are found through the reference column's index, so the cost follows the
    /// number of such rows, not the size of the table.</summary>
    Task<IReadOnlyList<(long Id, string Text)>> ListUnlinkedOriginalsAsync(
        AppTable child, AppField reference, AppField original, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Points each row of <paramref name="parentIdByRowId"/> (row Id → parent Record ID) at its parent and clears its
    /// <paramref name="original"/> text — only while its reference is still blank, so a link made in the meantime is never
    /// overwritten. Returns the rows changed (PublicId and the parent Record ID they now hold).</summary>
    Task<IReadOnlyList<(Guid PublicId, long ParentId)>> LinkUnlinkedReferencesAsync(
        AppTable child, AppField reference, AppField original, IReadOnlyDictionary<long, long> parentIdByRowId,
        System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        FilterGroup? filterTree = null,
        IReadOnlyList<SortSpec>? sortFields = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    /// <summary>Every live row matching <paramref name="filterTree"/> in ONE query — no paging, no row cap and
    /// no command timeout (the request's cancellation token still aborts it). Rows are decrypted. For reports
    /// that must see every matching record to filter, sort or total computed (Formula) fields in memory.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAllMatchingAsync(
        AppTable table, IReadOnlyList<AppField> fields,
        FilterGroup? filterTree = null,
        IReadOnlyList<SortSpec>? sortFields = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    /// <summary>The next <paramref name="batchSize"/> live rows with Id greater than <paramref name="afterId"/>,
    /// ordered by Id (keyset paging), decrypted, with no command timeout. Call repeatedly with the last Id
    /// returned until a batch comes back short or empty to stream every matching record without holding them
    /// all in memory — a summary over computed (Formula) fields accumulates its groups batch by batch.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListBatchAfterIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, long afterId, int batchSize,
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default);

    Task<int> CountAsync(AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Sum / non-blank count / row count of the given physical numeric (or checkbox) columns across
    /// EVERY record matching <paramref name="filterTree"/> — one SQL round trip with the same WHERE
    /// as <see cref="ListAsync"/>/<see cref="CountAsync"/>, so the numbers always agree with the
    /// grid's own filtering. Only for non-computed, non-encrypted columns (callers route formula and
    /// encrypted columns through an in-memory pass instead). Keyed by the field's Fid.
    /// </summary>
    Task<IReadOnlyDictionary<long, Records.ColumnTotal>> AggregateColumnsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<AppField> columns,
        FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default);

    /// <summary>Returns true if the table has at least one non-deleted row — an EXISTS check, not a
    /// COUNT, so it stays cheap on tables with millions of records. Used to gate whether a field's
    /// encryption setting can still be changed (see FieldDetailResponse.HasRecords).</summary>
    Task<bool> HasAnyRecordsAsync(AppTable table, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, object?>> GetSearchableFieldsAsync(Guid recordPublicId, CancellationToken ct = default);
    Task<long> GetRecordIdByPublicIdAsync(AppTable table, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, long>> GetRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<long> GetActiveRecordIdByPublicIdAsync(AppTable table, Guid publicId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, long>> GetActiveRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    /// <summary>Inserts many records with multi-row INSERT statements inside <paramref name="transaction"/> (required). Each row is
    /// stored exactly as <see cref="CreateAsync"/> would store it (same encryption, same blank handling, same search-index
    /// message); only the round trips differ. Returns the new records' PublicIds in the order of <paramref name="rows"/>. A unique-index
    /// violation fails the whole call (nothing is inserted) with <c>ConflictException</c>, like the single insert.</summary>
    Task<IReadOnlyList<Guid>> CreateManyAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<long, object?>> rows,
        System.Data.IDbTransaction transaction, CancellationToken ct = default,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    /// <summary>For each requested key, the live records whose (plain, unencrypted Text or Number) <paramref name="field"/> equals
    /// it: (value as stored, PublicId). Keys are looked up in chunks, so the number of keys is not limited.</summary>
    Task<IReadOnlyList<(object? Value, Guid PublicId)>> FindRecordsByFieldValuesAsync(
        AppTable table, AppField field, IReadOnlyCollection<object> keys, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);

    /// <summary>Writes the same set of field values to every row in <paramref name="recordIds"/> in a
    /// single UPDATE statement (implicitly atomic — either every matched row is updated or none is).
    /// Used by mass update, after constraint validation has already passed for every record.</summary>
    Task<int> MassUpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> recordIds,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null, System.Data.IDbTransaction? transaction = null);

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
    Task<bool> HasValueDuplicateAsync(AppTable table, AppField field, object value, long? excludeRecordId = null, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>True when the field's values are stored encrypted (so SQL cannot compare them).</summary>
    Task<bool> FieldValuesAreEncryptedAsync(AppTable table, AppField field, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Every non-empty value the field holds across the table's non-deleted rows, decrypted — one scan, for callers
    /// that check many values against it in memory (the per-value <see cref="HasValueDuplicateAsync"/> on an encrypted field
    /// decrypts the whole column each time).</summary>
    Task<IReadOnlyList<string>> GetAllValuesForUniqueCheckAsync(AppTable table, AppField field, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Batched form of <see cref="HasValueDuplicateAsync"/> for a plain (not encrypted) Text/Number field:
    /// the positions in <paramref name="values"/> whose value some non-deleted row already holds, decided by the
    /// database's own comparison, in one query per chunk instead of one table scan per value.</summary>
    Task<IReadOnlySet<int>> GetExistingValueIndexesAsync(AppTable table, AppField field, IReadOnlyList<object> values, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted row has a NULL or empty-string value in the field's
    /// column (a candidate key field must be populated on every row).</summary>
    Task<bool> HasNullsAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Returns true if any non-deleted rows have a non-null, non-empty value in the field's column.</summary>
    Task<bool> HasAnyDataAsync(AppTable table, AppField field, CancellationToken ct = default);

    /// <summary>Run a GROUP BY aggregation query for Summary (and Chart) reports. When <paramref name="seriesField"/>
    /// is supplied (Chart reports only), groups by both <paramref name="groupByField"/> and it, and each result
    /// row additionally carries a "SeriesValue" key.</summary>
    /// <param name="sort">Overrides the default "ORDER BY every group level, ascending" — Summary-
    /// only (RunSummaryAsync resolves ReportDefinition.SummarySortFields into this; Chart never
    /// populates it, so its rows keep the pre-existing default order). Null/empty = unchanged
    /// default behavior.</param>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table,
        IReadOnlyList<(AppField Field, string Mode)> groupByFields,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        AppField? seriesField = null,
        string seriesMode = "EqualValues",
        IReadOnlyList<SummarizeSortSpec>? sort = null,
        CancellationToken ct = default);

    /// <param name="onlyRowIds">When given, only these rows are read (the rows the user's role may view).</param>
    Task<(IReadOnlyList<string> Values, bool ExceedsLimit)> GetDistinctFieldValuesAsync(
        AppTable table, AppField field, int limit, string? subField = null, CancellationToken ct = default,
        IReadOnlyCollection<long>? onlyRowIds = null);

    Task<int> SanitizeTableEncryptedDataAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default);
    Task<IReadOnlyList<SearchIndexDocument>> GetFieldBackfillBatchAsync(long tenantId, long appId, long tableId, long fieldId, bool isNullify, int page, int pageSize, CancellationToken ct = default);
}
