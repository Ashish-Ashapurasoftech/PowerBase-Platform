using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.ShareGroupWithApp;

public class ShareGroupWithAppCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepository;

    public ShareGroupWithAppCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(ShareGroupWithAppCommand command, CancellationToken ct = default)
    {
        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var shared = await _groupRepository.ShareWithAppsAsync(
            command.GroupPublicId,
            command.AppPublicIds,
            _queryContext.UserId,
            command.AppRolePublicId,
            ct);

        if (shared)
        {
            await _auditRepository.LogActivityAsync(
                AuditActions.Updated,
                AuditEntityTypes.Group,
                command.GroupPublicId.ToString(),
                $"Shared group '{existingGroup.Name}' with {command.AppPublicIds.Count()} app(s)",
                newValues: JsonSerializer.Serialize(new { AppPublicIds = command.AppPublicIds, AppRolePublicId = command.AppRolePublicId }),
                ct: ct);
        }

        return shared;
    }
}
