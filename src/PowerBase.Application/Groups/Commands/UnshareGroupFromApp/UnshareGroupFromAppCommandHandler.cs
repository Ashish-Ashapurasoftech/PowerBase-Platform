using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.UnshareGroupFromApp;

public class UnshareGroupFromAppCommandHandler
{
    private readonly IGroupRepository _groupRepository;

    public UnshareGroupFromAppCommandHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<bool> HandleAsync(UnshareGroupFromAppCommand command, CancellationToken ct = default)
    {
        return await _groupRepository.UnshareFromAppAsync(command.GroupPublicId, command.AppPublicId, ct);
    }
}
