namespace PowerBase.API.Models.Tables;

public record UpdateTableRequest(string Name, string? SingularLabel, string? PluralLabel, string? Description, string? Icon);
