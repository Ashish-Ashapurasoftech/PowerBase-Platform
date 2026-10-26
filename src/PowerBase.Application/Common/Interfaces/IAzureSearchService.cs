namespace PowerBase.Application.Common.Interfaces;

public interface IAzureSearchService
{
    Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);
    Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default);

    /// <summary>
    /// Same upload as <see cref="BulkIndexRecordsAsync"/>, but returns every document Azure AI Search did not accept
    /// (after retrying throttled ones) instead of dropping it. A request-level failure still throws. The indexer uses
    /// this to dead-letter or redeliver exactly the messages whose documents failed.
    /// </summary>
    Task<IReadOnlyList<SearchIndexFailure>> BulkIndexRecordsWithResultsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default);
    Task BulkDeleteRecordsAsync(long tenantId, long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, string? indexName, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, string? indexName, CancellationToken ct = default);

    /// <summary>
    /// Every record id matching <paramref name="odataFilter"/>, with no ceiling on how many, as pages of at most 1,000 ids
    /// (the most a single query returns). Pages come in a fixed order, each with a cursor: pass a page's
    /// <see cref="AiSearchIdPage.NextCursor"/> as <paramref name="afterCursor"/> to continue after it, so an interrupted
    /// read resumes instead of starting over. The sequence simply ends when there is nothing more. Unlike
    /// <see cref="SearchRecordsByFilterAsync"/> it never truncates, which is why that method is left as it is for Reports.
    /// </summary>
    IAsyncEnumerable<AiSearchIdPage> SearchRecordIdsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, string? afterCursor = null, CancellationToken ct = default);
    IAsyncEnumerable<AiSearchIdPage> SearchRecordIdsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, string? afterCursor, string? indexName, CancellationToken ct = default);
    Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, int page, int pageSize, CancellationToken ct = default);
    Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, int page, int pageSize, string? indexName, CancellationToken ct = default);
    Task<(IReadOnlyList<Guid> Ids, long TotalCount)> SearchRecordsPagedAsync(
        long tenantId, long tableId, string? searchText, string? odataFilter, int page, int pageSize,
        IReadOnlyList<string>? orderBy = null, string? indexName = null, CancellationToken ct = default);
    Task<(IReadOnlyList<GlobalSearchResult> Items, long? TotalCount)> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task EnsureTableSchemaAsync(long tenantId, long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default);
    Task PurgeTableRecordsAsync(long tenantId, long tableId, CancellationToken ct = default);
    Task PurgeAppRecordsAsync(long tenantId, long appId, CancellationToken ct = default);
    Task ResetIndexAsync(string? indexName = null, CancellationToken ct = default);
    Task<bool> CreateIndexAsync(string? indexName = null, bool force = false, CancellationToken ct = default);
    bool IsEnabled { get; }
    bool IsGridSearchEnabled { get; }
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}

/// <summary>One page of matching record ids, and the cursor that resumes after it.</summary>
public record AiSearchIdPage(IReadOnlyList<Guid> Ids, string NextCursor)
{
    /// <summary>The cursor of a page that ends the read: nothing is left after it.</summary>
    public const string EndCursor = "~";
}

public record GlobalSearchResult(Guid PublicId, long AppId, long TableId, IReadOnlyDictionary<string, string> Fields);

public record SearchIndexDocument(
    long TenantId,
    long AppId,
    long TableId,
    Guid PublicId,
    IReadOnlyDictionary<long, object?> Values,
    IReadOnlyDictionary<string, object?>? SlotValues = null,
    string? CustomIndexName = null,
    IReadOnlySet<string>? SearchTextSlots = null);

/// <summary>One document Azure AI Search rejected or could not take. <see cref="Retryable"/> is true for 409/422/429/503.</summary>
public record SearchIndexFailure(string Key, int Status, string? Error, bool Retryable);
