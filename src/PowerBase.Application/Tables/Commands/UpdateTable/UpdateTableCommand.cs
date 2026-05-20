namespace PowerBase.Application.Tables.Commands.UpdateTable;

public record UpdateTableCommand(Guid TablePublicId, string Name, string? SingularLabel, string? PluralLabel, string? Description, string? Icon);
