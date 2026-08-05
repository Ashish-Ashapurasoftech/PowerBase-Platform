using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.AppTokens.Common;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.CreateAppToken;

public class CreateAppTokenCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IAppRepository _appRepository;
    private readonly IQueryContext _queryContext;

    public CreateAppTokenCommandHandler(
        IAppTokenRepository appTokenRepository,
        IAppRepository appRepository,
        IQueryContext queryContext)
    {
        _appTokenRepository = appTokenRepository;
        _appRepository = appRepository;
        _queryContext = queryContext;
    }

    public async Task<AppTokenCreatedDto> HandleAsync(CreateAppTokenCommand command, CancellationToken cancellationToken = default)
    {
        var appId = await _appRepository.GetIdByPublicIdAsync(command.AppPublicId, cancellationToken);
        if (appId == 0)
        {
            throw new NotFoundException("App", command.AppPublicId);
        }

        var rawSecret = "pb_at_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        
        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawSecret))).ToLowerInvariant();
        var tokenPrefix = rawSecret;

        var appToken = new AppToken
        {
            PublicId = Guid.NewGuid(),
            TenantId = _queryContext.TenantId,
            AppId = appId,
            CreatedByUserId = _queryContext.UserId,
            TokenName = command.TokenName,
            Description = command.Description,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var createdToken = await _appTokenRepository.CreateAsync(appToken, cancellationToken);

        return new AppTokenCreatedDto
        {
            PublicId = createdToken.PublicId,
            AppPublicId = command.AppPublicId,
            CreatedByUserId = createdToken.CreatedByUserId,
            TokenName = createdToken.TokenName,
            Description = createdToken.Description,
            TokenPrefix = createdToken.TokenPrefix,
            IsActive = createdToken.IsActive,
            CreatedAt = createdToken.CreatedAt,
            LastUsedAt = createdToken.LastUsedAt,
            PlainTextToken = rawSecret
        };
    }
}
