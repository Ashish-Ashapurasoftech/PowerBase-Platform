using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Groups.Commands.CreateGroup;

public class CreateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAppRoleRepository _appRoleRepository;

    public CreateGroupCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAppRoleRepository appRoleRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _appRoleRepository = appRoleRepository;
    }

    public async Task<GroupDto> HandleAsync(CreateGroupCommand command, CancellationToken ct = default)
    {
        var exists = await _groupRepository.ExistsByNameAsync(command.Name, null, ct);
        if (exists)
            throw new InvalidOperationException($"A group with the name '{command.Name}' already exists.");

        long? appRoleId = null;
        string? appRoleName = null;
        if (command.AppRolePublicId.HasValue)
        {
            var role = await _appRoleRepository.GetByPublicIdAsync(command.AppRolePublicId.Value, ct);
            if (role == null)
                throw new KeyNotFoundException($"App role '{command.AppRolePublicId.Value}' not found.");
            appRoleId = role.Id;
            appRoleName = role.Name;
        }

        var group = new Group
        {
            PublicId = Guid.NewGuid(),
            Name = command.Name.Trim(),
            Description = command.Description?.Trim(),
            AppRoleId = appRoleId,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = _queryContext.UserId
        };

        var created = await _groupRepository.CreateAsync(group, ct);

        return new GroupDto
        {
            PublicId = created.PublicId,
            Name = created.Name,
            Description = created.Description,
            AppRolePublicId = command.AppRolePublicId,
            AppRoleName = appRoleName,
            MemberCount = 0,
            CreatedOn = created.CreatedOn
        };
    }
}
