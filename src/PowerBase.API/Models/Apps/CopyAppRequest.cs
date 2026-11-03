namespace PowerBase.API.Models.Apps;

/// <summary>Copy App – Schema Only: the new app gets the same structure and no records.</summary>
public class CopyAppRequest
{
    /// <summary>Name of the copy; the dialog pre-fills "Copy of {app name}".</summary>
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>False ("Only me", default): only you get access, as Administrator.
    /// True ("Copy users in their roles"): the original's users and groups keep their roles. Nobody is emailed.</summary>
    public bool CopyUsers { get; set; } = false;

    /// <summary>Optional id chosen by the caller; while the copy runs, its progress is at
    /// GET apps/{appId}/copy/progress/{progressId}.</summary>
    public Guid? ProgressId { get; set; }
}
