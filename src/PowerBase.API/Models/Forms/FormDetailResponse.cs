namespace PowerBase.API.Models.Forms;

public class FormDetailResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsQuickPeekForm { get; init; }
    public bool AutoAddNewFields { get; init; }
    public bool ShowBuiltInFields { get; init; }
    public List<string> SaveOptions { get; init; } = [];
    public int DisplayOrder { get; init; }
    public string RowVersion { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
}
