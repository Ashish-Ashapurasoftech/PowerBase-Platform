using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Filtering on one part of an Address field — "Address: City is Pune" — as the report filter offers it (Street 1, Street 2, City,
/// State/Region, Postal Code, Country). A filter rule names it as <c>fid_N.part</c>; the part is a property of the stored address JSON,
/// compared as text.
/// </summary>
public static class PipelineAddressFieldFilter
{
    /// <summary>The parts a rule can filter on: the properties of the stored address.</summary>
    public static readonly IReadOnlyList<string> Parts = new[] { "street1", "street2", "city", "state", "zip", "country" };

    /// <summary>A rule field like <c>fid_7.city</c> (or <c>Address.city</c>) → the Address field and the part.</summary>
    public static bool TryResolve(string? fieldRef, IReadOnlyList<AppField> fields, out AppField field, out string part)
    {
        field = null!;
        part = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldRef)) return false;
        var dot = fieldRef.LastIndexOf('.');
        if (dot <= 0 || dot == fieldRef.Length - 1) return false;
        var candidate = fieldRef[(dot + 1)..].Trim().ToLowerInvariant();
        if (!Parts.Contains(candidate)) return false;
        var baseRef = fieldRef[..dot].Trim();
        var resolved = fields.FirstOrDefault(f => f.Fid.HasValue && $"fid_{f.Fid}".Equals(baseRef, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => !string.IsNullOrEmpty(f.Label) && f.Label.Equals(baseRef, StringComparison.OrdinalIgnoreCase))
            ?? fields.FirstOrDefault(f => f.Name.Equals(baseRef, StringComparison.OrdinalIgnoreCase));
        if (resolved == null || !string.Equals(resolved.TypeCode, "Address", StringComparison.OrdinalIgnoreCase)) return false;
        field = resolved;
        part = candidate;
        return true;
    }

    /// <summary>One part of a stored address (its JSON); empty when the address is blank, not JSON, or has no such part.</summary>
    public static string ReadPart(object? address, string part)
    {
        var json = address switch
        {
            null => null,
            JsonElement element => element.ValueKind == JsonValueKind.Object ? element.GetRawText() : element.ToString(),
            _ => address.ToString()
        };
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }
}
