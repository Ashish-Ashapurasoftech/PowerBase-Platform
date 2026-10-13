namespace PowerBase.Application.Relationships;

/// <summary>
/// Which existing child fields may be reused as a relationship's reference field, given the type of
/// the parent's key field (the value shown in the picker). The frontend wizard mirrors this table.
///   Text key     → Text, Formula - text, Text - Multiple Choice
///   Numeric key  → Number, Formula - numeric, Currency   (Number and Currency parents alike)
///   Date key     → Date, Formula - date, Formula - date/time, Date/Time
///   anything else → the same type only
/// </summary>
public static class ReferenceFieldCompatibility
{
    private static readonly string[] TextFamily = ["Text", "Formula_Text", "MultiSelect"];
    private static readonly string[] NumericFamily = ["Number", "Formula_Number", "Currency"];
    private static readonly string[] DateFamily = ["Date", "Formula_Date", "Formula_DateTime", "DateTime"];

    public static IReadOnlyList<string> AllowedChildTypeCodes(string parentKeyTypeCode) => parentKeyTypeCode switch
    {
        "Text" or "TextMultiLine" or "SingleSelect" or "MultiSelect" or "Email" or "Phone" or "Url" => TextFamily,
        "Number" or "Currency" => NumericFamily,
        "Date" or "DateTime" => DateFamily,
        _ => [parentKeyTypeCode],
    };

    public static bool IsAllowed(string parentKeyTypeCode, string childTypeCode) =>
        AllowedChildTypeCodes(parentKeyTypeCode).Contains(childTypeCode, StringComparer.OrdinalIgnoreCase);
}
