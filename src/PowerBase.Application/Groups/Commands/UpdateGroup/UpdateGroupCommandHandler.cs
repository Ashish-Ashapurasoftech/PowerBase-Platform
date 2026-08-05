using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.UpdateGroup;

public class UpdateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAppRoleRepository _appRoleRepository;

    public UpdateGroupCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAppRoleRepository appRoleRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _appRoleRepository = appRoleRepository;
    }

    public async Task<bool> HandleAsync(UpdateGroupCommand command, CancellationToken ct = default)
    {
        var exists = await _groupRepository.ExistsByNameAsync(command.Name, command.PublicId, ct);
        if (exists)
            throw new InvalidOperationException($"A group with the name '{command.Name}' already exists.");

        long? appRoleId = null;
        if (command.AppRolePublicId.HasValue)
        {
            var role = await _appRoleRepository.GetByPublicIdAsync(command.AppRolePublicId.Value, ct);
            if (role == null)
                throw new KeyNotFoundException($"App role '{command.AppRolePublicId.Value}' not found.");
            appRoleId = role.Id;
        }

        var updated = await _groupRepository.UpdateAsync(
            command.PublicId,
            command.Name.Trim(),
            command.Description?.Trim(),
            appRoleId,
            _queryContext.UserId,
            ct);

        if (!updated)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        return true;
    }
}
