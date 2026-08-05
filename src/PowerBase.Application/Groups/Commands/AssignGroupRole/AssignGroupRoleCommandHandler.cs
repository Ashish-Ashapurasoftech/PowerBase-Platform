using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.AssignGroupRole;

public class AssignGroupRoleCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IAppRoleRepository _appRoleRepository;
    private readonly IQueryContext _queryContext;

    public AssignGroupRoleCommandHandler(
        IGroupRepository groupRepository, 
        IAppRoleRepository appRoleRepository, 
        IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _appRoleRepository = appRoleRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(AssignGroupRoleCommand command, CancellationToken ct = default)
    {
        long? appRoleId = null;
        if (command.AppRolePublicId.HasValue)
        {
            var role = await _appRoleRepository.GetByPublicIdAsync(command.AppRolePublicId.Value, ct);
            if (role == null)
                throw new KeyNotFoundException($"App role '{command.AppRolePublicId.Value}' not found.");
            appRoleId = role.Id;
        }

        var group = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (group == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var updated = await _groupRepository.UpdateAsync(
            command.GroupPublicId,
            group.Name,
            group.Description,
            appRoleId,
            _queryContext.UserId,
            ct);

        return updated;
        }
}
