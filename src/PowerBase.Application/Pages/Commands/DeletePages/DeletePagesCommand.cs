namespace PowerBase.Application.Pages.Commands.DeletePages;

public record DeletePagesCommand(Guid AppPublicId, IReadOnlyList<Guid> PagePublicIds);
