namespace PowerBase.Domain.Constants;

public static class PhysicalNaming
{
    public static string TableName(long appTableId) => $"t_{appTableId}";
    public static string FullTableName(long appTableId) => $"data.t_{appTableId}";
    public static string ColumnName(int fid) => $"f_{fid}";
    /// <summary>Second physical column for range field types (end / max).</summary>
    public static string EndColumnName(int fid) => $"f_{fid}_e";

    public static bool IsRangeTypeCode(string typeCode) =>
        typeCode is "DateRange" or "NumericRange";

    /// <summary>Field types computed at read time (Formula) — they have no physical storage column.</summary>
    public static bool IsComputedTypeCode(string typeCode) =>
        typeCode is "Formula";
}
