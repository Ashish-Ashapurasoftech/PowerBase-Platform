using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Settings;

/// <summary>
/// Saves the Behavior Settings of a Reference, Lookup or Summary field from Field Detail. The rest of such a field's
/// Settings JSON is the relationship's wiring (relationship, source / target field, function, criteria …), which only
/// the relationship commands may change — so only the nested <c>display</c> object
/// (<see cref="RelationshipDisplaySettings"/>) is taken from the request, and every other stored key is kept as it is.
/// </summary>
public static class RelationshipDisplaySettingsPolicy
{
    public const string PropertyName = "display";

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly RelationshipDisplaySettingsValidator Validator = new();

    /// <summary>True for the field types whose settings are saved through this policy.</summary>
    public static bool AppliesTo(string typeCode) =>
        typeCode is nameof(Domain.Enums.FieldTypeCode.Reference)
            or nameof(Domain.Enums.FieldTypeCode.Lookup)
            or nameof(Domain.Enums.FieldTypeCode.Summary);

    /// <summary>
    /// <paramref name="storedJson"/> with its <c>display</c> object replaced by the one in <paramref name="requestJson"/>.
    /// A request with no settings, or no <c>display</c> in them, leaves the stored settings as they are; a null or empty
    /// <c>display</c> removes it. A Reference drops the total / average / blank-as-0 opt-ins: its column holds the
    /// parent's Record ID#, so there is nothing of the key to add up. Throws <see cref="ValidationException"/> when the
    /// request's <c>display</c> is invalid.
    /// </summary>
    public static string? Merge(string typeCode, string? storedJson, string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson)) return storedJson;

        JsonObject? request;
        try { request = JsonNode.Parse(requestJson) as JsonObject; }
        catch (JsonException) { throw Invalid("Settings", "Settings must be a JSON object."); }
        if (request is null) throw Invalid("Settings", "Settings must be a JSON object.");

        var key = request.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, PropertyName, StringComparison.OrdinalIgnoreCase));
        if (key is null) return storedJson;

        var display = Parse(request[key]?.ToJsonString());
        if (display is not null && typeCode == nameof(Domain.Enums.FieldTypeCode.Reference))
        {
            display.TreatBlankAsZero = null;
            display.ShowTotalInReports = null;
            display.ShowAverageInReports = null;
            display.ShowTotalsRow = null;
        }
        var displayJson = display is null ? null : JsonSerializer.Serialize(display, WriteOpts);
        if (displayJson == "{}") displayJson = null;

        // Unchanged: hand the stored text back as it is, so the field gets no new version for a save that changed nothing.
        if (displayJson == StoredDisplayJson(storedJson)) return storedJson;

        JsonObject stored;
        try { stored = (string.IsNullOrWhiteSpace(storedJson) ? null : JsonNode.Parse(storedJson) as JsonObject) ?? new JsonObject(); }
        catch (JsonException) { stored = new JsonObject(); }
        foreach (var existing in stored.Select(p => p.Key).Where(k => string.Equals(k, PropertyName, StringComparison.OrdinalIgnoreCase)).ToList())
            stored.Remove(existing);
        if (displayJson is not null)
            stored[PropertyName] = JsonNode.Parse(displayJson);
        return stored.ToJsonString();
    }

    /// <summary>The request's <c>display</c> object, validated; null for a JSON null.</summary>
    private static RelationshipDisplaySettings? Parse(string? displayJson)
    {
        if (string.IsNullOrWhiteSpace(displayJson) || displayJson == "null") return null;
        var errors = Validator.Validate(displayJson);
        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]>(errors));
        return JsonSerializer.Deserialize<RelationshipDisplaySettings>(displayJson, ReadOpts);
    }

    /// <summary>The stored <c>display</c> object in the same normalized form <see cref="Merge"/> writes, or null.</summary>
    private static string? StoredDisplayJson(string? storedJson)
    {
        if (string.IsNullOrWhiteSpace(storedJson)) return null;
        try
        {
            if (JsonNode.Parse(storedJson) is not JsonObject stored) return null;
            var key = stored.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, PropertyName, StringComparison.OrdinalIgnoreCase));
            if (key is null || stored[key] is null) return null;
            var display = stored[key].Deserialize<RelationshipDisplaySettings>(ReadOpts);
            var json = display is null ? null : JsonSerializer.Serialize(display, WriteOpts);
            return json == "{}" ? null : json;
        }
        catch (JsonException) { return null; }
    }

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
