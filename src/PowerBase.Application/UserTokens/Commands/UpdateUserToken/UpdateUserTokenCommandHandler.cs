using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserToken;

public class UpdateUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public UpdateUserTokenCommandHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<bool> HandleAsync(UpdateUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        var existingToken = await _userTokenRepository.GetByPublicIdAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
        if (existingToken == null || existingToken.UserId != _queryContext.UserId)
        {
            throw new NotFoundException("UserToken", command.PublicId);
        }

        var result = await _userTokenRepository.UpdateDetailsAsync(
            existingToken.Id,
            command.TokenName,
            command.Description,
            command.AccessAllApps,
            command.AllowedAppPublicIds,
            cancellationToken
        );

        return result;
    }
}
