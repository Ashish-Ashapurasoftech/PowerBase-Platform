namespace PowerBase.Application.Tables.Commands.UpdateTable;

public record UpdateTableCommand(
    Guid PublicId,
    string Name,
    string? SingularLabel,
    string? PluralLabel,
    string? Description,
    string? Icon);
