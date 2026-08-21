namespace PowerBase.Application.Tables.Commands.BulkDeleteTables;

public record BulkDeleteTablesCommand(Guid AppPublicId, IReadOnlyList<Guid> PublicIds);
