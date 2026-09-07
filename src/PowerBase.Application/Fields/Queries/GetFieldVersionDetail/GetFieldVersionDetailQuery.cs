namespace PowerBase.Application.Fields.Queries.GetFieldVersionDetail;

public record GetFieldVersionDetailQuery(Guid TablePublicId, Guid FieldPublicId, int Version);
