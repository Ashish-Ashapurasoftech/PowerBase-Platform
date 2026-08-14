using FluentValidation;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus;

public class UpdateUserTokenStatusCommandValidator : AbstractValidator<UpdateUserTokenStatusCommand>
{
    public UpdateUserTokenStatusCommandValidator()
    {
        RuleFor(x => x.PublicIds)
            .NotEmpty().WithMessage("At least one token PublicId must be provided.");
    }
}
