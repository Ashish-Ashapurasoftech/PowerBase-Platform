using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public class CreateAppRoleCommandValidator : AbstractValidator<CreateAppRoleCommand>
{
    public CreateAppRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}
