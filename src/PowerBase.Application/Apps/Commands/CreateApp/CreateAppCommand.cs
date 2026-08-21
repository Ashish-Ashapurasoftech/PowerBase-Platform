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
    string Label,
    string TypeCode,
    string? Settings = null,
    bool IsEncrypted = false,
    /// <summary>Internal-only escape hatch — never populated from the public Create App request.
    /// Used exclusively by PBL/QBL app import to preserve a field's exact original Name. Null
    /// (the normal case) auto-generates Name from Label.</summary>
    string? Name = null
);

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color,
    IReadOnlyList<TableSpec> Tables,
    bool IsEncrypted = false
);
