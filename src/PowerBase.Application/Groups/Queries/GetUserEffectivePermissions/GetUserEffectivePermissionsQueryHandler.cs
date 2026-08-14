using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Queries.GetUserEffectivePermissions;

public class GetUserEffectivePermissionsQueryHandler
{
    private readonly IAppUserRepository _appUserRepository;

    public GetUserEffectivePermissionsQueryHandler(IAppUserRepository appUserRepository)
    {
        _appUserRepository = appUserRepository;
    }

    public async Task<UserEffectivePermissionsDto> HandleAsync(GetUserEffectivePermissionsQuery query, CancellationToken ct = default)
    {
        return await _appUserRepository.GetUserEffectivePermissionsAsync(query.UserPublicId, ct);
    }
}
