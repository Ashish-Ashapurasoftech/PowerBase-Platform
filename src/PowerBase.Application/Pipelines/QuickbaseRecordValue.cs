using System.Globalization;
using System.Text.RegularExpressions;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// How Create Record / Update Record read a mapped value for a number or checkbox field, as Quickbase does:
/// a number is read as written ("1234.5", "-3", "1e3"), so "1,234.50" or "$1,234.50" fails the step with
/// "Validation error: in field **amount**: not a valid number"; a checkbox reads true / yes / y / 1 as checked and
/// any other text ("maybe") as unchecked rather than failing. Other types are left to the step's own parsing.
/// </summary>
public static class QuickbaseRecordValue
{
    private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Numeric", "Currency", "Percent", "Integer", "Float", "Number", "Rating", "Duration" };
    private static readonly HashSet<string> CheckboxTypes = new(StringComparer.OrdinalIgnoreCase) { "Checkbox", "Boolean", "Bool" };
    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase) { "Date", "Date_Time", "DateTime", "Timestamp" };

    public static bool IsDateType(string? typeCode) => typeCode != null && DateTypes.Contains(typeCode);

    /// <summary>The field's key in Quickbase's messages: its name in snake case ("Start Date" becomes "start_date").</summary>
    public static string Key(string label) =>
        Regex.Replace(label.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    public static string InvalidDate(string label, string value) =>
        $"Validation error: in field **{Key(label)}**: Validation error: Invalid date/time value: {value.Trim()}";

    public static (bool Handled, object? Value) Read(string value, AppField field, string label)
    {
        var type = field.TypeCode ?? "";
        if (string.IsNullOrWhiteSpace(value) || !(NumberTypes.Contains(type) || CheckboxTypes.Contains(type)))
            return (false, null);
        var text = value.Trim();
        if (CheckboxTypes.Contains(type))
            return (true, text.ToLowerInvariant() is "y" || (PipelineChoiceValues.TryParseCheckbox(text, out var on) && on));
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (true, number);
        var message = $"Validation error: in field **{Key(label)}**: not a valid number";
        throw new PipelineMappingException(message, new FormatException(message));
    }

    private static readonly Regex UniqueMessage = new("^'(?<label>.+)' must be unique \u2014 this value is already in use[.]$");

    /// <summary>A record that breaks a Unique field, worded as Quickbase reports it ("…The field named "Key" with field id 12
    /// requires a unique value.; Error Code: 51; Transaction ID: …", a new id for each error, as Quickbase gives one); null when the error is not that.</summary>
    public static ValidationException? AsQuickbaseUnique(ValidationException error, IEnumerable<AppField> fields)
    {
        var messages = error.Errors.Values.SelectMany(m => m).ToList();
        if (messages.Count == 0) messages.Add(error.Message);
        var match = messages.Select(m => UniqueMessage.Match(m)).FirstOrDefault(m => m.Success);
        if (match is null) return null;
        var label = match.Groups["label"].Value;
        var field = fields.FirstOrDefault(f => string.Equals(f.Label ?? f.Name, label, StringComparison.Ordinal))
            ?? fields.FirstOrDefault(f => string.Equals(f.Name, label, StringComparison.Ordinal));
        var fid = field?.Fid is int n ? $" with field id {n}" : "";
        var text = "Quickbase reported an error: Trying to add a non-unique value to a field marked unique; " +
            $"The field named \"{label}\"{fid} requires a unique value.; Error Code: 51; Transaction ID: {Guid.NewGuid()}";
        return new ValidationException(new Dictionary<string, string[]> { ["Unique"] = [text] });
    }
}
