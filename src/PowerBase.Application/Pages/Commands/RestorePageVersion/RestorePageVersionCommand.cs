namespace PowerBase.Application.Pages.Commands.RestorePageVersion;

public record RestorePageVersionCommand(Guid PagePublicId, int VersionNo, string ChangeNotes);
