using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateAppBranding;

public class UpdateAppBrandingCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateAppBrandingCommandHandler(IAppRepository appRepo, IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateAppBrandingCommand command, CancellationToken ct = default)
    {
        // Enforce App Administrator privileges explicitly
        await _appAccessService.RequireAppRoleAsync(command.AppPublicId, "Administrator", ct);

        var validator = new UpdateAppBrandingCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Merge onto the existing stored settings rather than requiring the caller to
        // resend both blobs every time — Branding and Layout are edited from separate
        // tabs in App Settings (Branding vs Styles/Layout), so a request commonly only
        // carries one of the two.
        var existing = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);

        var brandingStr = command.Branding is not null
            ? JsonSerializer.Serialize(command.Branding)
            : existing.Branding;
        var layoutStr = command.Layout is not null
            ? JsonSerializer.Serialize(command.Layout)
            : existing.LayoutSettings;

        var affected = await _appRepo.UpdateBrandingAsync(command.AppPublicId, brandingStr, layoutStr, ct);
        if (affected == 0)
            throw new NotFoundException("App", command.AppPublicId);
    }
}
