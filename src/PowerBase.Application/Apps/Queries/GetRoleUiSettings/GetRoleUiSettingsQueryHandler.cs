using PowerBase.Application.Apps.RoleUi;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.GetRoleUiSettings;

public record GetRoleUiSettingsQuery(Guid AppPublicId, Guid RolePublicId);

/// <summary>A role's "User interface" settings (what the role's users don't see). Nothing stored = nothing hidden.</summary>
public class GetRoleUiSettingsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;

    public GetRoleUiSettingsQueryHandler(IAppRepository appRepo, IAppRoleRepository appRoleRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
    }

    public async Task<RoleUiSettings> HandleAsync(GetRoleUiSettingsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var role = await _appRoleRepo.GetByPublicIdAsync(query.RolePublicId, ct);
        if (role is null || role.AppId != appId)
            throw new NotFoundException("AppRole", query.RolePublicId);

        return RoleUiSettings.Parse(await _appRoleRepo.GetUiSettingsJsonAsync(role.Id, ct));
    }
}
