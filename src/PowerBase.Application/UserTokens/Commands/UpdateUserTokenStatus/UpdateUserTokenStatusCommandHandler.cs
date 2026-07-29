using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus;

public class UpdateUserTokenStatusCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public UpdateUserTokenStatusCommandHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(UpdateUserTokenStatusCommand command, CancellationToken cancellationToken = default)
    {
        var requestedIds = command.PublicIds.Distinct().ToList();
        var existingIds = (await _userTokenRepository.GetExistingPublicIdsAsync(requestedIds, _queryContext.TenantId, cancellationToken)).ToHashSet();

        var missingIds = requestedIds.Where(id => !existingIds.Contains(id)).ToList();
        if (missingIds.Any())
        {
            throw new NotFoundException("UserTokens", "selected");
        }

        return await _userTokenRepository.UpdateStatusAsync(requestedIds, _queryContext.TenantId, command.IsActive, cancellationToken);
    }
}
