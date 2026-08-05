using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.DeleteGroup;

public class DeleteGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public DeleteGroupCommandHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(DeleteGroupCommand command, CancellationToken ct = default)
    {
        var deleted = await _groupRepository.DeleteAsync(command.PublicId, _queryContext.UserId, ct);
        if (!deleted)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        return true;
    }
}
