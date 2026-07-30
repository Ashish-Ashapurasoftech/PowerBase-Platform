namespace PowerBase.Application.Pages.Commands.DuplicatePage;

public record DuplicatePageCommand(Guid PagePublicId, string? NewName);
