using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.UserTokens.Commands.RevokeUserToken;

public class RevokeUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public RevokeUserTokenCommandHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(RevokeUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        return await _userTokenRepository.RevokeAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
    }
}
