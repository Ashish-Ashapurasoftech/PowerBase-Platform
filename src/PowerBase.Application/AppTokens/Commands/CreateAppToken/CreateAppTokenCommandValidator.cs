using FluentValidation;

namespace PowerBase.Application.AppTokens.Commands.CreateAppToken;

public class CreateAppTokenCommandValidator : AbstractValidator<CreateAppTokenCommand>
{
    public CreateAppTokenCommandValidator()
    {
        RuleFor(x => x.AppPublicId)
            .NotEmpty().WithMessage("App ID is required.");

        RuleFor(x => x.TokenName)
            .NotEmpty().WithMessage("Token name is required.")
            .MaximumLength(200).WithMessage("Token name cannot exceed 200 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description cannot exceed 1000 characters.");
    }
}
