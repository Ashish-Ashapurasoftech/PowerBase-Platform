using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.DeleteGroup;

public class DeleteGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepository;

    public DeleteGroupCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(DeleteGroupCommand command, CancellationToken ct = default)
    {
        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.PublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        var deleted = await _groupRepository.DeleteAsync(command.PublicId, _queryContext.UserId, ct);
        if (!deleted)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        await _auditRepository.LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Group,
            command.PublicId.ToString(),
            $"Group deleted: {existingGroup.Name}",
            ct: ct);

        return true;
    }
}
