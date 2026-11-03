namespace PowerBase.Domain.Constants;

/// <summary>
/// How a Reference (or reference-proxy) form element picks the columns its dropdown shows — Quickbase's
/// "Select using record picker". Stored in <see cref="Entities.FormElement.RecordPickerMode"/>; null means <see cref="Default"/>.
/// </summary>
public static class RecordPickerModes
{
    /// <summary>The parent table's default record picker (Advanced settings).</summary>
    public const string Default = "default";
    /// <summary>The first columns, filter and sort of a Table report on the parent table.</summary>
    public const string Report = "report";
    /// <summary>One Lookup field of the relationship: its source parent field is the only column.</summary>
    public const string Field = "field";
}
