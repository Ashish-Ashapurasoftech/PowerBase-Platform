using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateAppVariable;

public class CreateAppVariableCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IAppAccessService _appAccessService;

    public CreateAppVariableCommandHandler(
        IAppRepository appRepo,
        IAppVariableRepository variableRepo,
        IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _variableRepo = variableRepo;
        _appAccessService = appAccessService;
    }

    public async Task<AppVariable> HandleAsync(CreateAppVariableCommand command, CancellationToken ct = default)
    {
        var validator = new CreateAppVariableCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        await _appAccessService.RequireByAppPublicIdAsync(command.AppPublicId, AppAccess.Admin, ct);

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var count = await _variableRepo.CountAsync(appId, ct);
        if (count >= 10)
            throw new ConflictException("App variables limit of 10 reached.");

        if (await _variableRepo.NameExistsAsync(appId, command.Name, ct))
            throw new DuplicateException("AppVariable", "name", command.Name);

        var variable = new AppVariable
        {
            AppId = appId,
            Name = command.Name,
            Value = command.Value,
            Description = command.Description
        };

        var publicId = await _variableRepo.CreateAsync(variable, ct);

        var created = await _variableRepo.GetByPublicIdAsync(appId, publicId, ct);
        return created ?? throw new NotFoundException("AppVariable", publicId);
    }
}
