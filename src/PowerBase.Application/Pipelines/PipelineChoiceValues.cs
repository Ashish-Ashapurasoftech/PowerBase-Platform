using System.Text.Encodings.Web;
using System.Text.Json;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Values a pipeline step maps into a Single-select / Multi-select Text field. A Multi-select column stores a JSON
/// array of its choices — what the record form saves — while a step's mapping is text: choices picked in the field's
/// Select tab ("Red, Blue"), Quickbase-style "Red;Blue", the value of another step's multi-select field (itself a
/// JSON array) or any mix of those. Written as-is, such text was one item named "Red, Blue" that the form could not
/// show as two picks.
/// </summary>
public static class PipelineChoiceValues
{
    private static readonly char[] Delimiters = { ',', ';', '\n', '\r' };

    // Choices stay readable in the stored text ("ગુજરાતી", not "ગ…"); both forms read back the same.
    private static readonly JsonSerializerOptions StoredJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The choices a Single/Multi-select field's settings define (<c>{"options":[...]}</c>), or none.</summary>
    public static IReadOnlyList<string> ChoicesOf(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(settings);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("options", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array) continue;
                return property.Value.EnumerateArray()
                    .Where(option => option.ValueKind == JsonValueKind.String)
                    .Select(option => option.GetString()!)
                    .Where(option => !string.IsNullOrWhiteSpace(option))
                    .ToList();
            }
        }
        catch (JsonException) { /* not JSON settings — no choices */ }
        return Array.Empty<string>();
    }

    /// <summary>
    /// The items of a mapped multi-choice value, in order and without repeats. Items are separated by commas,
    /// semicolons or line breaks; a choice of the field that itself holds one of those ("Smith, John") is kept whole,
    /// and an embedded JSON array (another multi-select value) contributes each of its items.
    /// </summary>
    public static List<string> SplitItems(string? value, IReadOnlyList<string>? choices = null)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return items;
        var text = value;
        var delimitedChoices = (choices ?? Array.Empty<string>())
            .Where(choice => choice.IndexOfAny(Delimiters) >= 0)
            .OrderByDescending(choice => choice.Length)
            .ToList();

        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || Array.IndexOf(Delimiters, text[i]) >= 0) { i++; continue; }

            var whole = delimitedChoices.FirstOrDefault(choice =>
                string.CompareOrdinal(text, i, choice, 0, choice.Length) == 0 && EndsItem(text, i + choice.Length));
            if (whole != null)
            {
                items.Add(whole);
                i += whole.Length;
                continue;
            }

            if (text[i] == '[' && TryReadJsonArray(text, i, out var arrayItems, out var arrayEnd))
            {
                items.AddRange(arrayItems);
                i = arrayEnd;
                continue;
            }

            var next = text.IndexOfAny(Delimiters, i);
            if (next < 0) next = text.Length;
            var item = text[i..next].Trim();
            if (item.Length > 0) items.Add(item);
            i = next;
        }

        return items.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The text a Multi-select column stores for a mapped value: a JSON array of its items, or null when it holds none.
    /// A value that already is a JSON array (another multi-select field's value) is kept exactly as it was.
    /// </summary>
    public static string? ToStoredMultiSelect(string? value, string? settings = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (IsJsonArray(trimmed)) return trimmed;
        var items = SplitItems(trimmed, ChoicesOf(settings));
        return items.Count == 0 ? null : JsonSerializer.Serialize(items, StoredJson);
    }

    /// <summary>
    /// A mapped Checkbox value: the Yes / No a step's Select tab writes, true / false (a referenced checkbox), or 1 / 0 —
    /// any casing, surrounding spaces ignored. False when the text is none of those.
    /// </summary>
    public static bool TryParseCheckbox(string? value, out bool result)
    {
        result = false;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                result = true;
                return true;
            case "false" or "no" or "0":
                return true;
            default:
                return false;
        }
    }

    private static bool EndsItem(string text, int position)
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
        return position >= text.Length || Array.IndexOf(Delimiters, text[position]) >= 0;
    }

    /// <summary>An embedded JSON array ends an item, or is followed straight by another one (two references' values side by side).</summary>
    private static bool EndsArray(string text, int position)
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
        return EndsItem(text, position) || text[position] == '[';
    }

    private static bool IsJsonArray(string text)
    {
        if (!text.StartsWith('[') || !text.EndsWith(']')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Reads a JSON array that starts at <paramref name="start"/> and ends an item; "[Draft]" and other text that
    /// merely begins with a bracket is not one.</summary>
    private static bool TryReadJsonArray(string text, int start, out List<string> items, out int end)
    {
        items = new List<string>();
        end = start;
        for (var close = text.IndexOf(']', start); close >= 0; close = text.IndexOf(']', close + 1))
        {
            if (!EndsArray(text, close + 1)) continue;
            try
            {
                using var doc = JsonDocument.Parse(text[start..(close + 1)]);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var item = (element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText())?.Trim();
                    if (!string.IsNullOrEmpty(item) && element.ValueKind != JsonValueKind.Null) items.Add(item);
                }
                end = close + 1;
                return true;
            }
            catch (JsonException) { /* not complete yet — try the next closing bracket */ }
        }
        return false;
    }
}
