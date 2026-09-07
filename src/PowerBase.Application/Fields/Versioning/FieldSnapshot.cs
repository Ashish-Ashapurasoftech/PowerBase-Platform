using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.Versioning;

/// <summary>Every field-settings property that participates in versioning — the same surface
/// <see cref="PowerBase.Application.Fields.Commands.UpdateField.UpdateFieldCommandHandler"/> already
/// persists via IAppFieldRepository.UpdateAsync. Deliberately excludes Name/TypeCode (immutable
/// after creation) and IsSystem/Fid/CreatedOn (not user-editable settings).</summary>
public record FieldSnapshot(
    string? Label,
    string? Description,
    bool IsRequired,
    string? DefaultValue,
    bool IsSearchable,
    bool IsSortable,
    bool IsFilterable,
    bool IsReportable,
    bool IsAuditable,
    bool IsUnique,
    bool IsEncrypted,
    string? Settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static FieldSnapshot From(AppField field) => new(
        field.Label, field.Description, field.IsRequired, field.DefaultValue,
        field.IsSearchable, field.IsSortable, field.IsFilterable, field.IsReportable,
        field.IsAuditable, field.IsUnique, field.IsEncrypted, field.Settings);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static FieldSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<FieldSnapshot>(json, JsonOptions)
        ?? throw new InvalidOperationException("Stored field version snapshot could not be parsed.");
}
