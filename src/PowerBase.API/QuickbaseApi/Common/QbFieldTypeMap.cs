namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>Quickbase description of one of our fields: REST fieldType, REST/XML mode and XML base_type.</summary>
public sealed record QbFieldType(string FieldType, string Mode, string BaseType);

/// <summary>
/// Maps our field TypeCodes to Quickbase field type names (developer.quickbase.com "Field Types";
/// XML field_type/base_type as used by API_GetFieldProperties). Mode is "" for plain fields,
/// "formula", "lookup", "summary" or "virtual" (report link).
/// </summary>
public static class QbFieldTypeMap
{
    private static readonly Dictionary<string, (string FieldType, string BaseType)> Plain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Text"] = ("text", "text"),
        ["TextMultiLine"] = ("text-multi-line", "text"),   // live Quickbase reports multi-line text as its own type
        ["RichText"] = ("rich-text", "text"),
        ["SingleSelect"] = ("text-multiple-choice", "text"),
        ["MultiSelect"] = ("multitext", "text"),
        ["Number"] = ("numeric", "float"),
        ["NumericRange"] = ("numeric", "float"),
        ["Currency"] = ("currency", "float"),
        ["Percent"] = ("percent", "float"),
        ["Rating"] = ("rating", "float"),
        ["Date"] = ("date", "int64"),
        ["DateRange"] = ("date", "int64"),
        ["DateTime"] = ("timestamp", "int64"),
        ["Time"] = ("timeofday", "int64"),
        ["Duration"] = ("duration", "int64"),
        ["Boolean"] = ("checkbox", "bool"),
        ["Email"] = ("email", "text"),
        ["Phone"] = ("phone", "text"),
        ["Url"] = ("url", "text"),
        ["File"] = ("file", "text"),
        ["Address"] = ("address", "text"),
        ["User"] = ("user", "text"),
        ["MultiUser"] = ("multiuser", "text"),
        ["RecordId"] = ("recordid", "int64"),
        ["Reference"] = ("numeric", "int64"),
        ["ReportLink"] = ("dblink", "text"),
    };

    private static readonly Dictionary<string, (string FieldType, string BaseType)> FormulaResults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Formula_Text"] = ("text", "text"),
        ["Formula_RichText"] = ("rich-text", "text"),
        ["Formula_Number"] = ("numeric", "float"),
        ["Formula_Date"] = ("date", "int64"),
        ["Formula_DateTime"] = ("timestamp", "int64"),
        ["Formula_Time"] = ("timeofday", "int64"),
        ["Formula_Duration"] = ("duration", "int64"),
        ["Formula_Bool"] = ("checkbox", "bool"),
        ["Formula_User"] = ("user", "text"),
        ["Formula_ListUser"] = ("multiuser", "text"),
        ["Formula_Url"] = ("url", "text"),
        ["Formula_Email"] = ("email", "text"),
        ["Formula_Phone"] = ("phone", "text"),
        ["Formula_MultiSelect"] = ("multitext", "text"),
        ["Formula"] = ("text", "text"),
    };

    /// <param name="typeCode">Our AppField.TypeCode.</param>
    /// <param name="resultTypeCode">For Lookup/Summary fields: the TypeCode of the value they return, when known.</param>
    public static QbFieldType Map(string? typeCode, string? resultTypeCode = null)
    {
        var code = typeCode ?? "";
        if (FormulaResults.TryGetValue(code, out var f)) return new(f.FieldType, "formula", f.BaseType);
        if (code.Equals("Lookup", StringComparison.OrdinalIgnoreCase)) return Derived(resultTypeCode, "lookup");
        if (code.Equals("Summary", StringComparison.OrdinalIgnoreCase)) return Derived(resultTypeCode ?? "Number", "summary");
        if (code.Equals("ReportLink", StringComparison.OrdinalIgnoreCase)) return new("dblink", "", "text"); // live Quickbase: mode ""
        // Action buttons have no stored value; Quickbase buttons are formula-URL fields.
        if (code.StartsWith("ActionButton", StringComparison.OrdinalIgnoreCase)) return new("url", "formula", "text");
        if (Plain.TryGetValue(code, out var p)) return new(p.FieldType, "", p.BaseType);
        return new("text", "", "text");
    }

    private static QbFieldType Derived(string? resultTypeCode, string mode)
    {
        var inner = Map(resultTypeCode);
        // Quickbase reports a lookup of a multiple-choice field as plain text (measured on a live realm).
        if (mode == "lookup" && inner.FieldType == "text-multiple-choice") inner = inner with { FieldType = "text" };
        return inner with { Mode = mode };
    }
}
