using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CopyApp;

public class CopyAppCommandValidator : AbstractValidator<CopyAppCommand>
{
    public CopyAppCommandValidator()
    {
        // Same limits as CreateAppCommandValidator.
        RuleFor(x => x.AppPublicId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
    }
}
