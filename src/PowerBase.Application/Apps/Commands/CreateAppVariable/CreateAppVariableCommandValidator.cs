using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CreateAppVariable;

public class CreateAppVariableCommandValidator : AbstractValidator<CreateAppVariableCommand>
{
    public CreateAppVariableCommandValidator()
    {
        RuleFor(x => x.AppPublicId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Value).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
    }
}
