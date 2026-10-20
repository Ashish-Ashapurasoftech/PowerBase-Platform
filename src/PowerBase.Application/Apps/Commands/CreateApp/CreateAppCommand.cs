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
    bool SeedDefaultViews = true,
    /// <summary>Internal-only (QBL/PBL import): keep this exact <c>_DBID_*</c> alias instead of generating one from the name,
    /// so formulas that name the table by its source alias keep resolving. Ignored when blank or already taken.</summary>
    string? Alias = null
);

public record AppFieldSpec(
    string Label,
    string TypeCode,
    string? Settings = null,
    bool IsEncrypted = false,
    /// <summary>Internal-only escape hatch — never populated from the public Create App request.
    /// Used exclusively by PBL/QBL app import to preserve a field's exact original Name. Null
    /// (the normal case) auto-generates Name from Label.</summary>
    string? Name = null,
    /// <summary>Internal-only (QBL/PBL import): initial values carried over from the source field. Null = PowerBase default.</summary>
    string? Description = null,
    string? HelpText = null,
    bool IsRequired = false,
    string? DefaultValue = null,
    bool IsAuditable = false,
    bool? IsSearchable = null,
    bool? IsReportable = null
);

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color,
    IReadOnlyList<TableSpec> Tables,
    bool IsEncrypted = false
);
