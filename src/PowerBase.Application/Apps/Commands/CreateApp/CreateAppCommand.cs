namespace PowerBase.Application.Apps.Commands.CreateApp;

public record TableSpec(
    string Name,
    string? SingularLabel = null,
    string? PluralLabel = null,
    string? Icon = null,
    string? Description = null,
    string? Config = null,
    IReadOnlyList<AppFieldSpec>? Fields = null
);

public record AppFieldSpec(
    string Name,
    string TypeCode,
    string? Settings = null
);

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color,
    IReadOnlyList<TableSpec> Tables
);
