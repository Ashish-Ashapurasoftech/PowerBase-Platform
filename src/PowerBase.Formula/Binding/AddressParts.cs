using System.Text.Json;

namespace PowerBase.Formula.Binding;

/// <summary>
/// An Address field in a formula: <c>[Address]</c> is the whole address as text (its non-empty parts joined with ", ") and
/// <c>[Address : Street 1]</c> one part of it, as in Quickbase. The stored value is JSON with the keys below.
/// </summary>
public static class AddressParts
{
    private static readonly string[] Order = ["street1", "street2", "city", "state", "zip", "country"];

    private static readonly Dictionary<string, string> KeyByLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["street 1"] = "street1", ["street1"] = "street1", ["street"] = "street1", ["address 1"] = "street1", ["address line 1"] = "street1",
        ["street 2"] = "street2", ["street2"] = "street2", ["address 2"] = "street2", ["address line 2"] = "street2",
        ["city"] = "city",
        ["state"] = "state", ["state/region"] = "state", ["state / region"] = "state", ["region"] = "state", ["province"] = "state",
        ["postal code"] = "zip", ["postalcode"] = "zip", ["zip"] = "zip", ["zip code"] = "zip", ["zipcode"] = "zip", ["postcode"] = "zip",
        ["country"] = "country",
    };

    /// <summary>The stored key of a sub-field label ("Street 1" gives street1), or null when it is not an address part.</summary>
    public static string? KeyOf(string label) => KeyByLabel.TryGetValue(label.Trim(), out var key) ? key : null;

    /// <summary>Splits <c>Address : Street 1</c> into the field name and the part label; false when there is no part.</summary>
    public static bool TrySplit(string reference, out string fieldName, out string part)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0 || colon == reference.Length - 1) { fieldName = part = string.Empty; return false; }
        fieldName = reference[..colon].Trim();
        part = reference[(colon + 1)..].Trim();
        return fieldName.Length > 0 && part.Length > 0;
    }

    /// <summary>The text a formula sees for a stored address: the whole address, or one part when <paramref name="key"/> is given.</summary>
    public static string Text(object? raw, string? key)
    {
        if (raw is null) return string.Empty;
        var json = raw is JsonElement element ? element.GetRawText() : Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return key is null ? json : string.Empty;

            string Part(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            return key is not null
                ? Part(key)
                : string.Join(", ", Order.Select(Part).Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        catch (JsonException)
        {
            return key is null ? json : string.Empty; // an older plain-text value is the whole address
        }
    }
}
