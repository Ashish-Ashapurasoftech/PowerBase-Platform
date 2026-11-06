namespace PowerBase.API.Models.Apps;

/// <summary>Copy App: the new app gets the same structure, and with <see cref="WithData"/> the records too.</summary>
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

    /// <summary>True ("Copy this application with data"): the records are copied too, with new record ids, and references
    /// point at the copied records. App administrators only. A big app can take longer than the HTTP response may: the copy
    /// still finishes, and its progress (then its result) stays readable under <see cref="ProgressId"/>.</summary>
    public bool WithData { get; set; } = false;

    /// <summary>With data: file attachments come along too (default); false leaves File fields empty in the copy.</summary>
    public bool IncludeFiles { get; set; } = true;
}
