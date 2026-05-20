using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateApp;

public class UpdateAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateAppCommandHandler(IAppRepository appRepo, IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateAppCommand command, CancellationToken ct = default)
    {
        var validator = new UpdateAppCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        await _appAccessService.RequireByAppPublicIdAsync(command.AppPublicId, AppAccess.Admin, ct);

        var affected = await _appRepo.UpdateAsync(command.AppPublicId, command.Name, command.Description, command.Icon, command.Color, ct);
        if (affected == 0)
            throw new NotFoundException("App", command.AppPublicId);
    }
}
