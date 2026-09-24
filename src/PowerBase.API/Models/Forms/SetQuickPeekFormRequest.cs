namespace PowerBase.API.Models.Forms;

/// <summary>Enabled true flags the form as a Quick Peek form; false removes the flag.</summary>
public class SetQuickPeekFormRequest
{
    public bool Enabled { get; init; }
}
