using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.GetRecordFilters;

public class GetRecordFiltersQueryHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;

    public GetRecordFiltersQueryHandler(IAppRoleRepository appRoleRepo, IAppRolePermissionRepository permRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
    }

    public async Task<IReadOnlyList<RecordFilterRow>> HandleAsync(GetRecordFiltersQuery query, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(query.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", query.RolePublicId);
        return await _permRepo.GetRecordFiltersAsync(role.Id, ct);
    }
}
