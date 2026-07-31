using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.UserTokens.Commands.RotateUserToken;

public class RotateUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public RotateUserTokenCommandHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<UserTokenCreatedDto> HandleAsync(RotateUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        var existingToken = await _userTokenRepository.GetByPublicIdAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
        if (existingToken == null || existingToken.UserId != _queryContext.UserId)
        {
            throw new NotFoundException("UserToken", command.PublicId);
        }

        var rawSecret = "pb_ut_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawSecret))).ToLowerInvariant();

        var rotated = await _userTokenRepository.RotateSecretAsync(existingToken.Id, tokenHash, rawSecret, cancellationToken);
        if (!rotated)
        {
            throw new Exception("Failed to rotate user token.");
        }

        var allowedAppPublicIds = existingToken.AccessAllApps 
            ? Enumerable.Empty<Guid>() 
            : await _userTokenRepository.GetAllowedAppPublicIdsAsync(existingToken.Id, existingToken.TenantId, cancellationToken);

        return new UserTokenCreatedDto
        {
            PublicId = existingToken.PublicId,
            TokenName = existingToken.TokenName,
            Description = existingToken.Description,
            TokenPrefix = rawSecret,
            IsActive = existingToken.IsActive,
            AccessAllApps = existingToken.AccessAllApps,
            CreatedAt = existingToken.CreatedAt,
            LastUsedAt = existingToken.LastUsedAt,
            AllowedAppPublicIds = allowedAppPublicIds,
            PlainTextToken = rawSecret
        };
    }
}
