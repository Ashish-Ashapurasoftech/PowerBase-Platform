namespace PowerBase.API.Models.Forms;

public class UpdateFormSettingsRequest
{
    public string Name { get; init; } = string.Empty;
    public bool AutoAddNewFields { get; init; }
    public bool ShowBuiltInFields { get; init; }
    public string SaveOptions { get; init; } = string.Empty;
    public string RowVersion { get; init; } = string.Empty;
    /// <summary>Null leaves the Quick Peek flag unchanged.</summary>
    public bool? IsQuickPeekForm { get; init; }
    /// <summary>True makes this form the table's default Quick Peek form (implies flagged). The
    /// default cannot be un-flagged; false/null leave the default unchanged.</summary>
    public bool? IsQuickPeekDefault { get; init; }
}
