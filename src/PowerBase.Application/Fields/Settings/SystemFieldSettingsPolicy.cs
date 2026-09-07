using System.Text.Json;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Settings;

/// <summary>
/// Restricts a system field's Behavior Settings JSON to the handful of properties that are
/// actually meaningful for a system field, dropping everything else a custom field of the same
/// TypeCode would otherwise support. Applied only when <c>AppField.IsSystem</c> is true (see
/// UpdateFieldCommandHandler) — a custom Number/DateTime/User field is never touched by this.
///
/// Allow-list (the shared "Value display" trio plus each type's own applicable properties):
///   Number/Currency/Percent/Rating → DisplayBold, NoWrap, ColumnWidth only.
///   Date/DateTime                  → unchanged — DateSettings' full shape already IS this list.
///   User                           → DisplayBold, NoWrap, ColumnWidth, DisplayAs only.
/// </summary>
public static class SystemFieldSettingsPolicy
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    // CamelCase to match what the frontend's TS settings interfaces (and every settings JSON
    // blob the frontend itself ever writes) expect — this is a new write path, unlike the
    // validators, which only ever read/compare, never re-serialize.
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? RestrictSettingsJson(string typeCode, string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return settingsJson;

        try
        {
            switch (typeCode)
            {
                case "Number":
                case "Currency":
                case "Percent":
                case "Rating":
                {
                    var s = JsonSerializer.Deserialize<NumericSettings>(settingsJson, ReadOpts);
                    var restricted = new NumericSettings { DisplayBold = s?.DisplayBold, NoWrap = s?.NoWrap, ColumnWidth = s?.ColumnWidth };
                    return JsonSerializer.Serialize(restricted, WriteOpts);
                }
                case "User":
                {
                    var s = JsonSerializer.Deserialize<UserFieldSettings>(settingsJson, ReadOpts);
                    var restricted = new UserFieldSettings { DisplayBold = s?.DisplayBold, NoWrap = s?.NoWrap, ColumnWidth = s?.ColumnWidth, DisplayAs = s?.DisplayAs };
                    return JsonSerializer.Serialize(restricted, WriteOpts);
                }
                case "Date":
                case "DateTime":
                    // DateSettings' full property set already IS the system-field allow-list —
                    // nothing to strip.
                    return settingsJson;
                default:
                    // No system field uses any other TypeCode today; pass through unchanged
                    // rather than guess at an allow-list for a shape we don't restrict.
                    return settingsJson;
            }
        }
        catch (JsonException)
        {
            // Malformed input already fails the normal per-type validator earlier in the
            // handler pipeline — this is defense-in-depth, not the primary rejection path.
            return settingsJson;
        }
    }
}
