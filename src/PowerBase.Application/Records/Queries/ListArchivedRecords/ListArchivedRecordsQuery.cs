namespace PowerBase.Application.Records.Queries.ListArchivedRecords;

public record ListArchivedRecordsQuery(Guid TablePublicId, int Page = 1, int PageSize = 50);

public record ArchivedRecordsResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Items,
    int TotalCount,
    int Page,
    int PageSize);
