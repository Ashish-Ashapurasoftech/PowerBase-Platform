namespace PowerBase.Application.Common.Interfaces;

public interface IAzureSearchService
{
    Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);
    Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default);
    Task BulkDeleteRecordsAsync(long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tableId, string searchText, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tableId, string odataFilter, CancellationToken ct = default);
    Task<IReadOnlyList<GlobalSearchResult>> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, CancellationToken ct = default);
    Task EnsureTableSchemaAsync(long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default);
    bool IsGridSearchEnabled { get; }
}

public record GlobalSearchResult(Guid PublicId, long AppId, long TableId, IReadOnlyDictionary<string, string> Fields);

public record SearchIndexDocument(long TenantId, long AppId, long TableId, Guid PublicId, IReadOnlyDictionary<long, object?> Values);
