namespace PowerBase.API.Models.Forms;

public class FormListItemResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsQuickPeekForm { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime CreatedOn { get; init; }
}
