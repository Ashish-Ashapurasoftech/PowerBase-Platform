namespace PowerBase.Application.Tables.Commands.CreateTable;

public record CreateTableCommand(
    Guid AppPublicId,
    string Name,
    string? SingularLabel,
    string? PluralLabel,
    string? Description,
    string? Icon);
