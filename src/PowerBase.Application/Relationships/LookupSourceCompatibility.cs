using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Which parent fields a Lookup may switch its "Value field" to (Lookup field options), Quickbase-style: only fields of
/// the same type as the lookup shows now ("Text (lookup)" → text fields), so it keeps its type and whatever is built on
/// it keeps working. Types are compared as the type of value a field holds (<see cref="FieldValueTypes"/>), so a
/// formula matches the stored type it returns: Formula-Text is Text, Formula-URL is URL, Formula-Numeric is Numeric.
/// Plain-text variants (Text, Multi-line text, Text multiple choice) count as one type; every other type — URL, Email,
/// Phone, Rich text, Currency, Percent, Date … — only matches itself.
/// </summary>
public static class LookupSourceCompatibility
{
    private static readonly HashSet<string> PlainText = new(StringComparer.Ordinal)
    {
        "Text", "TextMultiLine", "SingleSelect",
    };

    /// <summary>Whether a field can be a lookup's source at all (anything holding a value — not a Report Link or a button).</summary>
    public static bool CanBeSource(AppField field) =>
        field.Fid.HasValue && !field.IsDeleted
        && field.TypeCode != "ReportLink" && !PhysicalNaming.IsActionButtonTypeCode(field.TypeCode);

    /// <summary>The compatibility class of a value type (a ResultTypeCode such as "Text", "Url", "Currency"); null when unknown.</summary>
    public static string? KindOf(string? resultTypeCode)
    {
        if (string.IsNullOrWhiteSpace(resultTypeCode)) return null;
        return PlainText.Contains(resultTypeCode) ? "Text" : resultTypeCode;
    }

    /// <summary>True when a source producing <paramref name="candidateResultType"/> may replace one producing
    /// <paramref name="currentResultType"/>. A lookup whose current type can't be told accepts any source.</summary>
    public static bool IsCompatible(string? currentResultType, string? candidateResultType)
    {
        var current = KindOf(currentResultType);
        if (current is null) return true;
        return string.Equals(current, KindOf(candidateResultType), StringComparison.Ordinal);
    }
}
