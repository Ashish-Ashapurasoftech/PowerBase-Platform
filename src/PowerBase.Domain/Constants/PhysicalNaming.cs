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
}
