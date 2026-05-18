using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public record CreateAppRoleResult(Guid PublicId, string Name, bool IsDefault);

public class CreateAppRoleCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;

    public CreateAppRoleCommandHandler(IAppRepository appRepo, IAppRoleRepository appRoleRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
    }

    public async Task<CreateAppRoleResult> HandleAsync(CreateAppRoleCommand command, CancellationToken ct = default)
    {
        var validator = new CreateAppRoleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        if (await _appRoleRepo.NameExistsInAppAsync(appId, command.Name, ct))
            throw new DuplicateException("AppRole", "name", command.Name);

        var (_, publicId) = await _appRoleRepo.CreateAsync(new AppRole
        {
            AppId = appId,
            TenantId = _queryContext.TenantId,
            Name = command.Name,
            IsDefault = command.IsDefault,
            IsSystem = false,
        }, ct: ct);

        return new CreateAppRoleResult(publicId, command.Name, command.IsDefault);
    }
}
