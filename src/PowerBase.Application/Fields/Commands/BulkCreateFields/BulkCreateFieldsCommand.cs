namespace PowerBase.Application.Fields.Commands.BulkCreateFields;

public record BulkCreateFieldItem(
    string TypeCode,
    string Label,
    string? Description = null,
    bool IsRequired = false,
    bool IsAuditable = false,
    string? Settings = null,
    string? DefaultValue = null,
    bool IsEncrypted = false,
    /// <summary>Internal-only escape hatch — never populated from the public BulkCreateFields API request.
    /// Used exclusively by PBL/QBL app import to preserve a field's exact original Name (its stable
    /// third-party identifier) instead of regenerating one. Null (the normal case) auto-generates Name from Label.</summary>
    string? Name = null,
    /// <summary>Internal-only (QBL/PBL import) overrides. HelpText is stored as-is. A false for IsSearchable/IsReportable switches the
    /// type's default off; null or true keeps the type's own default (a type that cannot be searched/reported is never forced on).</summary>
    string? HelpText = null,
    bool? IsSearchable = null,
    bool? IsReportable = null,
    /// <summary>Internal-only (QBL/PBL import): keep this Fid (the source field's Quickbase id). Falls back to the next free number when it cannot be kept.</summary>
    int? Fid = null);

public record BulkCreateFieldsCommand(Guid TablePublicId, IReadOnlyList<BulkCreateFieldItem> Fields);
