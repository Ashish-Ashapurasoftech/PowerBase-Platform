using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Reports;

/// <summary>
/// Checks and tidies a report's row-colour setting (<see cref="ReportOptions.RowColorJson"/>):
/// <c>{"mode":"none"}</c>, <c>{"mode":"choice","fieldId":7,"colors":{"Open":"#ffe08a"}}</c> or <c>{"mode":"column","fieldId":12}</c>.
/// The report page does the colouring; the server only keeps the setting honest, so a saved report cannot hold a shape the page cannot read.
/// </summary>
public static class ReportRowColors
{
    public const int MaxJsonLength = 8000;
    public const int MaxChoiceColors = 200;
    private const int MaxChoiceLength = 255;

    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>What to store: null when there is no colouring ("none" or blank), otherwise the setting as given.</summary>
    public static string? Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && string.Equals(mode.GetString(), "none", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (JsonException) { /* an invalid value is reported by Validate; stored as given only if it passed there */ }
        return json.Trim();
    }

    /// <summary>The reason a setting cannot be saved, or null when it is fine (blank counts as fine: no colouring).</summary>
    /// <param name="validFieldIds">The ids of the fields the report's table has.</param>
    public static string? Validate(string? json, ISet<long> validFieldIds)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > MaxJsonLength) return $"Row colours are too long (at most {MaxJsonLength} characters).";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return "Row colours must be valid JSON."; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
                return "Row colours need a mode: none, choice or column.";

            var mode = modeEl.GetString()!.ToLowerInvariant();
            if (mode == "none") return null;
            if (mode != "choice" && mode != "column") return "Row colours mode must be none, choice or column.";

            if (!root.TryGetProperty("fieldId", out var idEl) || !idEl.TryGetInt64(out var fieldId) || fieldId <= 0)
                return "Row colours need the field to colour by.";
            // "column" may also name one of the report's own formula columns (reserved ids), which are not fields of the table.
            var known = validFieldIds.Contains(fieldId) || (mode == "column" && ReportFormulaFields.IsReportFormulaFid(fieldId));
            if (!known) return $"Row colours use an unknown field: {fieldId}.";
            if (mode == "column") return null;

            if (!root.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
                return "Row colours by choice need a colour for each choice.";

            var count = 0;
            foreach (var entry in colors.EnumerateObject())
            {
                if (++count > MaxChoiceColors) return $"Row colours may name at most {MaxChoiceColors} choices.";
                if (entry.Name.Length > MaxChoiceLength) return "A choice name in Row colours is too long.";
                if (entry.Value.ValueKind != JsonValueKind.String || !HexColor.IsMatch(entry.Value.GetString() ?? ""))
                    return $"The colour for '{entry.Name}' must look like #rrggbb.";
            }
            return null;
        }
    }
}
