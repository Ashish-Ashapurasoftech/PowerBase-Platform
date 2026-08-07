using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.UserTokens.Commands.RevokeUserToken;

public class RevokeUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public RevokeUserTokenCommandHandler(
        IUserTokenRepository userTokenRepository,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<bool> HandleAsync(RevokeUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        var existingToken = await _userTokenRepository.GetByPublicIdAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
        if (existingToken == null || existingToken.UserId != _queryContext.UserId)
        {
            throw new NotFoundException("UserToken", command.PublicId);
        }

        var result = await _userTokenRepository.RevokeAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
        if (result)
        {
            await _auditRepo.LogActivityAsync(
                AuditActions.Deleted,
                AuditEntityTypes.UserToken,
                existingToken.PublicId.ToString(),
                $"User token revoked: {existingToken.TokenName}",
                ct: cancellationToken);
        }

        return result;
    }
}
