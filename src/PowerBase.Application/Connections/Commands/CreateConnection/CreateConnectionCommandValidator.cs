using FluentValidation;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Connections.Commands.CreateConnection;

public class CreateConnectionCommandValidator : AbstractValidator<CreateConnectionCommand>
{
    public CreateConnectionCommandValidator()
    {
        // Only the token mode creates an account. 'current_user' is resolved against the
        // caller's existing permitted realms and must never reach this command.
        RuleFor(x => x.AuthMode)
            .NotEmpty()
            .Equal(PipelineAccountAuthModes.UserToken)
            .WithMessage("Only 'user_token' accounts are created here; 'current_user' selects an existing realm.");

        RuleFor(x => x.Subdomain)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[a-zA-Z0-9][a-zA-Z0-9-]*$")
            .WithMessage("Company subdomain may contain letters, numbers and hyphens only.");

        RuleFor(x => x.UserToken)
            .NotEmpty()
            .WithMessage("User Token is required.")
            .MaximumLength(512);

        RuleFor(x => x.Name)
            .MaximumLength(200)
            .When(x => !string.IsNullOrWhiteSpace(x.Name));
    }
}
