using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Groups.Commands.UpdateGroup;

public class UpdateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepository;

    public UpdateGroupCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(UpdateGroupCommand command, CancellationToken ct = default)
    {
        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.PublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        var exists = await _groupRepository.ExistsByNameAsync(command.Name, command.PublicId, ct);
        if (exists)
            throw new DuplicateException("Group", "name", command.Name);

        var oldValues = new
        {
            Name = existingGroup.Name,
            Description = existingGroup.Description
        };

        var updated = await _groupRepository.UpdateAsync(
            command.PublicId,
            command.Name.Trim(),
            command.Description?.Trim(),
            _queryContext.UserId,
            ct);

        if (!updated)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        var newValues = new
        {
            Name = command.Name.Trim(),
            Description = command.Description?.Trim()
        };

        await _auditRepository.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.Group,
            command.PublicId.ToString(),
            $"Group updated: {command.Name.Trim()}",
            oldValues: JsonSerializer.Serialize(oldValues),
            newValues: JsonSerializer.Serialize(newValues),
            ct: ct);

        return true;
    }
}
