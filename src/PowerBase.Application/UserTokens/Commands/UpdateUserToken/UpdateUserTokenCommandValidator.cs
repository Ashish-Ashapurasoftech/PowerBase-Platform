using FluentValidation;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserToken;

public class UpdateUserTokenCommandValidator : AbstractValidator<UpdateUserTokenCommand>
{
    public UpdateUserTokenCommandValidator()
    {
        RuleFor(x => x.TokenName)
            .NotEmpty().WithMessage("Token name is required.")
            .MaximumLength(200).WithMessage("Token name cannot exceed 200 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description cannot exceed 1000 characters.");
    }
}
