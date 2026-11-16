namespace PowerBase.Application.Apps.Commands.CopyApp;

/// <summary>Copy App: a new standalone app with the same structure, and with <paramref name="WithData"/> its records too.</summary>
public record CopyAppCommand(
    Guid AppPublicId,
    string Name,
    string? Description,
    /// <summary>False ("Only me", the default): only the person copying gets access, as Administrator.
    /// True ("Copy users in their roles"): every user and group of the original keeps its role in the copy. Nobody is emailed.</summary>
    bool CopyUsers = false,
    /// <summary>Chosen by the dialog: the copy's progress can be read under this id while it runs.</summary>
    Guid? ProgressId = null,
    /// <summary>True ("Copy this application with data"): the records come along, with new record ids. App administrators only.</summary>
    bool WithData = false,
    /// <summary>With data: file attachments come along too (the copy gets its own copy of every file and revision); false
    /// leaves File fields empty.</summary>
    bool IncludeFiles = true
);
