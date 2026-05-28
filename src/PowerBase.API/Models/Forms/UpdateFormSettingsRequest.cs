namespace PowerBase.API.Models.Forms;

public class UpdateFormSettingsRequest
{
    public string Name { get; init; } = string.Empty;
    public bool AutoAddNewFields { get; init; }
    public bool ShowBuiltInFields { get; init; }
    public string SaveOptions { get; init; } = string.Empty;
    public string RowVersion { get; init; } = string.Empty;
}
