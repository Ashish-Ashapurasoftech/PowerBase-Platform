namespace PowerBase.Application.Forms.Commands.DuplicateForm;

public record DuplicateFormCommand(Guid FormPublicId, string? Name = null);
