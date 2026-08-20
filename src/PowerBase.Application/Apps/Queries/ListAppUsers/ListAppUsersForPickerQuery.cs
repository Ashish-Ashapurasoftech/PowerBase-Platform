using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Queries.ListAppUsers;

public record ListAppUsersForPickerQuery(Guid AppPublicId);

public class ListAppUsersForPickerQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;

    public ListAppUsersForPickerQueryHandler(IAppRepository appRepo, IAppUserRepository appUserRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
    }

    public async Task<IReadOnlyList<AppUserResult>> HandleAsync(ListAppUsersForPickerQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var users = await _appUserRepo.ListForUserPickerAsync(appId, ct);

        return users.Select(u => new AppUserResult(
            u.PublicId,
            u.UserPublicId,
            u.UserName,
            u.UserEmail,
            u.RolePublicId,
            u.RoleName,
            u.Status,
            u.ShowInUserPickers,
            u.CreatedOn,
            u.IsOwner,
            u.IsFromGroup)).ToList();
    }
}
