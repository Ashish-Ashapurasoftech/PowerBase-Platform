using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.UpdateRoleFormOverrides;

public record RoleFormOverrideCommandDto(Guid? RoleId, Guid? EditFormId, Guid? AddFormId);

public record UpdateRoleFormOverridesCommand(Guid TableId, List<RoleFormOverrideCommandDto> Overrides);

public class UpdateRoleFormOverridesCommandHandler
{
    private readonly IFormRepository _formRepo;

    public UpdateRoleFormOverridesCommandHandler(IFormRepository formRepo)
    {
        _formRepo = formRepo;
    }

    public async Task HandleAsync(UpdateRoleFormOverridesCommand request, CancellationToken ct)
    {
        var overrides = request.Overrides.Select(o => (o.RoleId, o.EditFormId, o.AddFormId));
        await _formRepo.UpdateRoleFormOverridesAsync(request.TableId, overrides, ct);
    }
}
