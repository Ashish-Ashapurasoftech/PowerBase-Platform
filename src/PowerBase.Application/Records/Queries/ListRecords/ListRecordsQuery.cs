namespace PowerBase.Application.Records.Queries.ListRecords;

public record ListRecordsQuery(Guid TablePublicId, int Page, int PageSize);
