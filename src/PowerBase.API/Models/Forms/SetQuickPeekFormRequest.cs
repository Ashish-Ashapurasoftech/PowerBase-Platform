namespace PowerBase.API.Models.Forms;

/// <summary>FormId null clears the table's Quick Peek form.</summary>
public class SetQuickPeekFormRequest
{
    public Guid? FormId { get; init; }
}
