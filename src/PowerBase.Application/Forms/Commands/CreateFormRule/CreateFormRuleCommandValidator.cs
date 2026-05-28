using FluentValidation;

namespace PowerBase.Application.Forms.Commands.CreateFormRule;

public class CreateFormRuleCommandValidator : AbstractValidator<CreateFormRuleCommand>
{
    public CreateFormRuleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
