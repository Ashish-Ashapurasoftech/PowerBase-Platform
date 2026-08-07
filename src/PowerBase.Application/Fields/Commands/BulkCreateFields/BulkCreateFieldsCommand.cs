namespace PowerBase.Application.Fields.Commands.BulkCreateFields;

public record BulkCreateFieldItem(
    string TypeCode,
    string Label,
    string? Description = null,
    bool IsRequired = false,
    bool IsAuditable = false,
    string? Settings = null,
    string? DefaultValue = null,
    /// <summary>Internal-only escape hatch — never populated from the public BulkCreateFields API request.
    /// Used exclusively by PBL/QBL app import to preserve a field's exact original Name (its stable
    /// third-party identifier) instead of regenerating one. Null (the normal case) auto-generates Name from Label.</summary>
    string? Name = null);

public record BulkCreateFieldsCommand(Guid TablePublicId, IReadOnlyList<BulkCreateFieldItem> Fields);
