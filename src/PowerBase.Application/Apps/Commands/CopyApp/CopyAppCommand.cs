namespace PowerBase.Application.Apps.Commands.CopyApp;

/// <summary>Copy App – Schema Only: a new standalone app with the same structure and no records.</summary>
public record CopyAppCommand(
    Guid AppPublicId,
    string Name,
    string? Description,
    /// <summary>False ("Only me", the default): only the person copying gets access, as Administrator.
    /// True ("Copy users in their roles"): every user and group of the original keeps its role in the copy. Nobody is emailed.</summary>
    bool CopyUsers = false,
    /// <summary>Chosen by the dialog: the copy's progress can be read under this id while it runs.</summary>
    Guid? ProgressId = null
);
