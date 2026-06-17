using FluentValidation;

namespace PowerBase.Application.Tenants.Commands.CreateTenant;

public class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        When(x => x.ServerConfig is not null, () =>
        {
            RuleFor(x => x.ServerConfig!.Host).NotEmpty().MaximumLength(253);
            RuleFor(x => x.ServerConfig!.Port).InclusiveBetween(1, 65535);
            RuleFor(x => x.ServerConfig!.AdminLogin).NotEmpty().MaximumLength(128);
            RuleFor(x => x.ServerConfig!.AdminPassword).NotEmpty().MaximumLength(500);
        });
    }
}
