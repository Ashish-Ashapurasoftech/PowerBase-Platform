namespace PowerBase.Application.Apps.Commands.CreateApp;

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color,
    string TableName = "Table 1",
    string? TableSingularLabel = null,
    string? TablePluralLabel = null,
    string? TableIcon = null,
    string? TableDescription = null
);
