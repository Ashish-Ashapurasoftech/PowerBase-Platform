namespace PowerBase.Domain.Constants;

/// <summary>Record-access scope for table-level View/Modify permissions.</summary>
public static class RecordScopes
{
    public const string None        = "None";
    public const string OwnRecords  = "OwnRecords";
    public const string AllRecords  = "AllRecords";
}

/// <summary>Per-field access level within a table.</summary>
public static class FieldAccessLevels
{
    public const string View   = "View";   // read-only
    public const string Modify = "Modify"; // editable (default)
    public const string None   = "None";   // hidden
}

/// <summary>Whether a role uses default (full) field access or a custom per-field map for a table.</summary>
public static class TableFieldAccessLevels
{
    public const string FullAccess   = "FullAccess";
    public const string CustomAccess = "CustomAccess";
}
