using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateAppVariable;

public class UpdateAppVariableCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateAppVariableCommandHandler(
        IAppRepository appRepo,
        IAppVariableRepository variableRepo,
        IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _variableRepo = variableRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateAppVariableCommand command, CancellationToken ct = default)
    {
        // Enforce App Administrator privileges explicitly
        await _appAccessService.RequireAppRoleAsync(command.AppPublicId, "Administrator", ct);

        var validator = new UpdateAppVariableCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var existing = await _variableRepo.GetByPublicIdAsync(appId, command.PublicId, ct);
        if (existing == null)
            throw new NotFoundException("AppVariable", command.PublicId);

        // Check uniqueness excluding self
        if (!string.Equals(existing.Name, command.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (await _variableRepo.NameExistsAsync(appId, command.Name, ct))
                throw new DuplicateException("AppVariable", "name", command.Name);
        }

        await _variableRepo.UpdateAsync(appId, command.PublicId, command.Name, command.Value, command.Description, ct);
    }
}
