namespace PowerBase.Application.Pages.Commands.SetDefaultHome;

/// <summary>Marks the given Dashboard page as the app's default home page, or clears it
/// (PagePublicId = null) so the app falls back to its default landing behaviour.</summary>
public record SetDefaultHomeCommand(Guid AppPublicId, Guid? PagePublicId);
