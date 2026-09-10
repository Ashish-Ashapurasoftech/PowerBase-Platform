using PowerBase.Domain.Entities;

namespace PowerBase.Domain.Constants;

public static class PhysicalNaming
{
    public static string TableName(long appTableId) => $"t_{appTableId}";
    public static string FullTableName(long appTableId) => $"data.t_{appTableId}";
    public static string ColumnName(int fid) => $"f_{fid}";
    /// <summary>Second physical column for range field types (end / max).</summary>
    public static string EndColumnName(int fid) => $"f_{fid}_e";

    public static string GetPhysicalColumnName(AppField field)
    {
        return field.IsSystem && !string.IsNullOrWhiteSpace(field.PhysicalColumnName)
            ? field.PhysicalColumnName
            : ColumnName(field.Fid!.Value);
    }

    public static bool IsRangeTypeCode(string typeCode) =>
        typeCode is "DateRange" or "NumericRange";

    /// <summary>
    /// Field types computed at read time — they have no physical storage column.
    /// Formula values come from the formula engine; Lookup/Summary values come from the
    /// relationship projector (cross-table). Reference is NOT computed — it is a physical
    /// BIGINT foreign-key column. ActionButton is an interactive action type — its own
    /// value is never stored; clicking it writes to other fields via a dedicated endpoint.
    /// </summary>
    public static bool IsComputedTypeCode(string typeCode) =>
        typeCode is "Formula" or "Lookup" or "Summary" or "ReportLink"
        || IsActionButtonTypeCode(typeCode) || IsFormulaVariantTypeCode(typeCode);

    /// <summary>True for the generic 'Formula' TypeCode's per-variant codes (Formula_Text,
    /// Formula_Number, Formula_Time, …) — mirrors <see cref="IsActionButtonTypeCode"/>'s
    /// StartsWith pattern for the same reason: each variant is a distinct, independently
    /// selectable TypeCode, not a Settings.Variant discriminator on one generic row.</summary>
    public static bool IsFormulaVariantTypeCode(string typeCode) =>
        typeCode.StartsWith("Formula_", StringComparison.Ordinal);

    /// <summary>True for the generic 'ActionButton' TypeCode and its four per-variant codes
    /// (ActionButton_Signature/File/Prompt/Data). Different tenant databases can have either
    /// shape in core.FieldType depending on when they were provisioned (some only ever got the
    /// original per-variant seed rows, others were migrated to the single generic row + a
    /// Settings.Variant discriminator) — every Action Button field behaves identically either
    /// way, so every TypeCode==/StartsWith("ActionButton") check in the app goes through this
    /// one helper instead of each re-deriving its own (and drifting, as happened before).</summary>
    public static bool IsActionButtonTypeCode(string typeCode) =>
        typeCode == "ActionButton" || typeCode.StartsWith("ActionButton_", StringComparison.Ordinal);

    /// <summary>True for the non-computed TypeCodes whose physical column genuinely stores text
    /// (NVARCHAR, including JSON-as-text for Select/File/Address) — an empty string is a
    /// meaningful value there (an intentionally blank Text field, say). Every other non-computed
    /// TypeCode's physical column is numeric/date/bit/bigint, where SQL Server can't implicitly
    /// convert an empty string and throws ("Error converting data type nvarchar to ..."). See
    /// RecordRepository's IsBlankForNonTextField, which uses this to turn a cleared grid/form cell
    /// into a real NULL write for those types instead of handing SQL Server a blank string.</summary>
    public static bool IsTextStoringTypeCode(string typeCode) =>
        typeCode is "Text" or "TextMultiLine" or "RichText" or "Email" or "Phone" or "Url"
            or "SingleSelect" or "MultiSelect" or "File" or "Address";
}
