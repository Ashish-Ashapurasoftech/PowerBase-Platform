using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.ShareGroupWithApp;

public class ShareGroupWithAppCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public ShareGroupWithAppCommandHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(ShareGroupWithAppCommand command, CancellationToken ct = default)
    {
        return await _groupRepository.ShareWithAppsAsync(
            command.GroupPublicId,
            command.AppPublicIds,
            _queryContext.UserId,
            ct);
    }
}
