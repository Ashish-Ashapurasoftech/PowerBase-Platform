using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.ListAppUsers;

public record AppUserResult(
    Guid PublicId,
    Guid UserPublicId,
    string UserName,
    string UserEmail,
    Guid RolePublicId,
    string RoleName,
    string Status,
    DateTime AddedOn,
    bool IsOwner);

public class ListAppUsersQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;

    public ListAppUsersQueryHandler(IAppRepository appRepo, IAppUserRepository appUserRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
    }

    public async Task<IReadOnlyList<AppUserResult>> HandleAsync(ListAppUsersQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var users = await _appUserRepo.ListByAppIdAsync(appId, ct);

        return users.Select(u => new AppUserResult(
            u.PublicId,
            u.UserPublicId,
            u.UserName,
            u.UserEmail,
            u.RolePublicId,
            u.RoleName,
            u.Status,
            u.CreatedOn,
            u.IsOwner)).ToList();
    }
}
