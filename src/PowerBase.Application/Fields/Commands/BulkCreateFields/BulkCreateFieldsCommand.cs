namespace PowerBase.Application.Fields.Commands.BulkCreateFields;

public record BulkCreateFieldItem(
    string TypeCode,
    string Name,
    string? Label = null,
    string? Description = null,
    bool IsRequired = false,
    bool IsAuditable = true,
    string? Settings = null,
    string? DefaultValue = null,
    bool IsEncrypted = false);

public record BulkCreateFieldsCommand(Guid TablePublicId, IReadOnlyList<BulkCreateFieldItem> Fields);
