namespace PowerBase.Application.Tables.Commands.CreateTable;

public record CreateTableCommand(
    Guid AppPublicId,
    string Name,
    string? SingularLabel,
    string? PluralLabel,
    string? Description,
    string? Icon,
    /// <summary>Optional custom fields created together with the table (like Create App's per-table
    /// Fields). Null/empty = only the system fields, as before.</summary>
    IReadOnlyList<CreateTableFieldSpec>? Fields = null);

public record CreateTableFieldSpec(
    string Label,
    string TypeCode,
    string? Settings = null,
    bool IsRequired = false,
    bool IsEncrypted = false);
