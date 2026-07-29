using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.UserTokens.Commands.CreateUserToken;

public class CreateUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public CreateUserTokenCommandHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<UserTokenCreatedDto> HandleAsync(CreateUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        var rawSecret = "pb_ut_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawSecret))).ToLowerInvariant();
        var tokenPrefix = rawSecret.Substring(0, 10) + "...";

        var userToken = new UserToken
        {
            PublicId = Guid.NewGuid(),
            TenantId = _queryContext.TenantId,
            UserId = _queryContext.UserId,
            TokenName = command.TokenName,
            Description = command.Description,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            IsActive = true,
            AccessAllApps = command.AccessAllApps,
            CreatedAt = DateTime.UtcNow
        };

        var createdToken = await _userTokenRepository.CreateAsync(userToken, command.AllowedAppPublicIds, cancellationToken);
        var allowedAppPublicIds = command.AccessAllApps 
            ? Enumerable.Empty<Guid>() 
            : await _userTokenRepository.GetAllowedAppPublicIdsAsync(createdToken.Id, cancellationToken);

        return new UserTokenCreatedDto
        {
            PublicId = createdToken.PublicId,
            TokenName = createdToken.TokenName,
            Description = createdToken.Description,
            TokenPrefix = createdToken.TokenPrefix,
            IsActive = createdToken.IsActive,
            AccessAllApps = createdToken.AccessAllApps,
            CreatedAt = createdToken.CreatedAt,
            LastUsedAt = createdToken.LastUsedAt,
            AllowedAppPublicIds = allowedAppPublicIds,
            PlainTextToken = rawSecret
        };
    }
}
