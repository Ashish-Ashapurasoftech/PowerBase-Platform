using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Groups.Commands.CreateGroup;

public class CreateGroupCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public CreateGroupCommandHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<GroupDto> HandleAsync(CreateGroupCommand command, CancellationToken ct = default)
    {
        var exists = await _groupRepository.ExistsByNameAsync(command.Name, null, ct);
        if (exists)
            throw new InvalidOperationException($"A group with the name '{command.Name}' already exists.");

        var group = new Group
        {
            PublicId = Guid.NewGuid(),
            Name = command.Name.Trim(),
            Description = command.Description?.Trim(),
            CreatedOn = DateTime.UtcNow,
            CreatedBy = _queryContext.UserId
        };

        var created = await _groupRepository.CreateAsync(group, ct);

        return new GroupDto
        {
            PublicId = created.PublicId,
            Name = created.Name,
            Description = created.Description,
            CreatedOn = created.CreatedOn
        };
    }
}
