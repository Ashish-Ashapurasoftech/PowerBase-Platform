using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Records.Queries.SearchGlobalRecords;

public record SearchGlobalRecordsQuery(string SearchText, long? AppId = null);

public record SearchGlobalRecordsResult(IReadOnlyList<SearchGlobalRecordsResultItem> Items);

public record SearchGlobalRecordsResultItem(Guid RecordId, Guid AppId, string AppName, Guid TableId, string TableName, string? TableIcon, string PrimaryText);
