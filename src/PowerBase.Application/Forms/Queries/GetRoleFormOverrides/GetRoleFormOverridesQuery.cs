using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Queries.GetRoleFormOverrides;

public record RoleFormOverrideDto(Guid? RoleId, string? RoleName, Guid? EditFormId, Guid? AddFormId);

public record GetRoleFormOverridesQuery(Guid TableId);

public class GetRoleFormOverridesQueryHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppRoleRepository _roleRepo;

    public GetRoleFormOverridesQueryHandler(
        IFormRepository formRepo,
        IAppTableRepository tableRepo,
        IAppRoleRepository roleRepo)
    {
        _formRepo = formRepo;
        _tableRepo = tableRepo;
        _roleRepo = roleRepo;
    }

    public async Task<IReadOnlyList<RoleFormOverrideDto>> HandleAsync(GetRoleFormOverridesQuery request, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(request.TableId, ct);
        var appRoles = await _roleRepo.ListDetailsByAppIdAsync(table.AppId, ct);
        var overrides = await _formRepo.GetRoleFormOverridesAsync(request.TableId, ct);

        var result = new List<RoleFormOverrideDto>();

        // 1. Everyone
        var everyoneOverride = overrides.FirstOrDefault(o => o.RolePublicId == null);
        result.Add(new RoleFormOverrideDto(null, null, everyoneOverride.EditFormPublicId, everyoneOverride.AddFormPublicId));

        // 2. All App Roles
        foreach (var role in appRoles)
        {
            var roleOverride = overrides.FirstOrDefault(o => o.RolePublicId == role.PublicId);
            result.Add(new RoleFormOverrideDto(role.PublicId, role.Name, roleOverride.EditFormPublicId, roleOverride.AddFormPublicId));
        }

        return result;
    }
}
