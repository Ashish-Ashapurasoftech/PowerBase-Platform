namespace PowerBase.Application.Common.Interfaces;

public interface IAzureSearchService
{
    Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);
    Task BulkIndexRecordsAsync(IEnumerable<SearchIndexDocument> documents, CancellationToken ct = default);
    Task BulkDeleteRecordsAsync(long tenantId, long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tenantId, long tableId, string searchText, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsByFilterAsync(long tenantId, long tableId, string odataFilter, CancellationToken ct = default);

    /// <summary>
    /// Every record id matching <paramref name="odataFilter"/>, with no ceiling on how many, as pages of at most 1,000 ids
    /// (the most a single query returns). Pages come in a fixed order, each with a cursor: pass a page's
    /// <see cref="AiSearchIdPage.NextCursor"/> as <paramref name="afterCursor"/> to continue after it, so an interrupted
    /// read resumes instead of starting over. The sequence simply ends when there is nothing more. Unlike
    /// <see cref="SearchRecordsByFilterAsync"/> it never truncates, which is why that method is left as it is for Reports.
    /// </summary>
    IAsyncEnumerable<AiSearchIdPage> SearchRecordIdsByFilterPagedAsync(
        long tenantId, long tableId, string odataFilter, string? afterCursor = null, CancellationToken ct = default);
    Task<(IReadOnlyList<GlobalSearchResult> Items, long? TotalCount)> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task EnsureTableSchemaAsync(long tenantId, long tableId, IEnumerable<(int Fid, bool IsSearchable, bool IsFilterable)> fields, CancellationToken ct = default);
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

public record SearchIndexDocument(long TenantId, long AppId, long TableId, Guid PublicId, IReadOnlyDictionary<long, object?> Values);
