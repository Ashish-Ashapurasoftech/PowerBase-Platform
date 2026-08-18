namespace PowerBase.Application.Apps.Commands.CreateApp;

public record TableSpec(
    string Name,
    string? SingularLabel = null,
    string? PluralLabel = null,
    string? Icon = null,
    string? Description = null,
    string? Config = null,
    IReadOnlyList<AppFieldSpec>? Fields = null,
    /// <summary>False when the caller will create this table's own forms/reports afterwards, so
    /// the seeded "Main Form"/"List All"/"List Changes" would only be duplicates. Defaults to
    /// true — normal app creation is unaffected. See <c>IAppSeeder</c>.</summary>
    bool SeedDefaultViews = true
);

public record AppFieldSpec(
    string Name,
    string TypeCode,
    string? Settings = null,
    bool IsEncrypted = false
);

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color,
    IReadOnlyList<TableSpec> Tables,
    bool IsEncrypted = false
);
