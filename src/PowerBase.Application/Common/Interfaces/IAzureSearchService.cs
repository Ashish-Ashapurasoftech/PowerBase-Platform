namespace PowerBase.Application.Common.Interfaces;

public interface IAzureSearchService
{
    Task IndexRecordAsync(long tenantId, long appId, long tableId, Guid publicId, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default);
    Task DeleteRecordAsync(long tableId, Guid publicId, CancellationToken ct = default);
    Task BulkDeleteRecordsAsync(long tableId, IReadOnlyList<Guid> publicIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SearchRecordsAsync(long tableId, string searchText, CancellationToken ct = default);
    Task<IReadOnlyList<GlobalSearchResult>> SearchGlobalAsync(long tenantId, string searchText, long? appId = null, CancellationToken ct = default);
}

public record GlobalSearchResult(Guid PublicId, long AppId, long TableId);
