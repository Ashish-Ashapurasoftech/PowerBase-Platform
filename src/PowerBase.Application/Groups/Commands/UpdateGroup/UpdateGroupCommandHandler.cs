using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.UpdateGroup;

public class UpdateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public UpdateGroupCommandHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(UpdateGroupCommand command, CancellationToken ct = default)
    {
        var exists = await _groupRepository.ExistsByNameAsync(command.Name, command.PublicId, ct);
        if (exists)
            throw new InvalidOperationException($"A group with the name '{command.Name}' already exists.");

        var updated = await _groupRepository.UpdateAsync(
            command.PublicId,
            command.Name.Trim(),
            command.Description?.Trim(),
            _queryContext.UserId,
            ct);

        if (!updated)
            throw new KeyNotFoundException($"Group '{command.PublicId}' not found.");

        return true;
    }
}
