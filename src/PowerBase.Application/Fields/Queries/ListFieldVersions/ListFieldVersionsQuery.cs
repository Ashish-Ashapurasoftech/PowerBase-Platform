namespace PowerBase.Application.Fields.Queries.ListFieldVersions;

public record ListFieldVersionsQuery(Guid TablePublicId, Guid FieldPublicId, int Page, int PageSize);
