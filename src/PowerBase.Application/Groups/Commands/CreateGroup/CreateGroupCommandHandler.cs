using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Groups.Commands.CreateGroup;

public class CreateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAppRoleRepository _appRoleRepository;
    private readonly IAuditRepository _auditRepository;

    public CreateGroupCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAppRoleRepository appRoleRepository,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _appRoleRepository = appRoleRepository;
        _auditRepository = auditRepository;
    }

    public async Task<GroupDto> HandleAsync(CreateGroupCommand command, CancellationToken ct = default)
    {
        var exists = await _groupRepository.ExistsByNameAsync(command.Name, null, ct);
        if (exists)
            throw new DuplicateException("Group", "name", command.Name);

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

        await _auditRepository.LogActivityAsync(
            AuditActions.Created,
            AuditEntityTypes.Group,
            created.PublicId.ToString(),
            $"Group created: {created.Name}",
            newValues: JsonSerializer.Serialize(new 
            { 
                Name = created.Name, 
                Description = created.Description, 
                AppRolePublicId = command.AppRolePublicId,
                AppRoleName = appRoleName
            }),
            ct: ct);

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
