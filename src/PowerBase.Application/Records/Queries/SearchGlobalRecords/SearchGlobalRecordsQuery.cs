using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Records.Queries.SearchGlobalRecords;

public record SearchGlobalRecordsQuery(string SearchText, long? AppId = null, int Page = 1, int PageSize = 50);

public record SearchGlobalRecordsResult(IReadOnlyList<SearchGlobalRecordsResultItem> Items, long TotalCount, int Page, int PageSize);

public record SearchGlobalRecordsResultItem(Guid RecordId, Guid AppId, string AppName, Guid TableId, string TableName, string? TableIcon, string PrimaryText);
