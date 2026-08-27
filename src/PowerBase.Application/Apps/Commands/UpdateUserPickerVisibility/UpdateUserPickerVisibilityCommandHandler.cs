using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateUserPickerVisibility;

public class UpdateUserPickerVisibilityCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;

    public UpdateUserPickerVisibilityCommandHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateUserPickerVisibilityCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        var appId = app.Id;

        // 1. Try resolving by AppUser assignment PublicId first
        var appUser = await _appUserRepo.GetByPublicIdAsync(command.UserPublicId, ct);
        if (appUser != null && appUser.AppId == appId)
        {
            await _appUserRepo.UpdateShowInUserPickersByAssignmentAsync(appId, appUser.PublicId, command.ShowInUserPickers, ct);

            await _auditRepo.LogActivityAsync(
                AuditActions.Updated,
                AuditEntityTypes.AppUser,
                appUser.UserId.ToString(),
                $"User picker visibility updated for app user {appUser.UserEmail} to {command.ShowInUserPickers}",
                appId: appId,
                ct: ct);
            return;
        }

        // 2. Fallback: resolve by core User PublicId
        var user = await _userRepo.GetByPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("User", command.UserPublicId);

        appUser = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct)
            ?? throw new NotFoundException("AppUser", command.UserPublicId);

        await _appUserRepo.UpdateShowInUserPickersAsync(appId, user.Id, command.ShowInUserPickers, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.AppUser,
            user.Id.ToString(),
            $"User picker visibility updated for app user {user.Email} to {command.ShowInUserPickers}",
            appId: appId,
            ct: ct);
    }
}
