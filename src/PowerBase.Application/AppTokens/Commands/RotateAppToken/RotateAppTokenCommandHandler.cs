using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.AppTokens.Common;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.RotateAppToken;

public class RotateAppTokenCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;

    public RotateAppTokenCommandHandler(IAppTokenRepository appTokenRepository, IQueryContext queryContext)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<AppTokenCreatedDto> HandleAsync(Guid appPublicId, Guid publicId, CancellationToken cancellationToken = default)
    {
        var existingToken = await _appTokenRepository.GetByPublicIdAsync(publicId, _queryContext.TenantId, appPublicId, cancellationToken);
        if (existingToken == null)
        {
            throw new NotFoundException("AppToken", publicId);
        }

        var rawSecret = "pb_at_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

        using var sha256 = SHA256.Create();
        var tokenHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawSecret))).ToLowerInvariant();
        var tokenPrefix = rawSecret;

        var rotated = await _appTokenRepository.RotateSecretAsync(existingToken.Id, tokenHash, tokenPrefix, cancellationToken);
        if (!rotated)
        {
            throw new NotFoundException("AppToken", publicId);
        }

        return new AppTokenCreatedDto
        {
            PublicId = existingToken.PublicId,
            AppPublicId = appPublicId,
            TokenName = existingToken.TokenName,
            Description = existingToken.Description,
            TokenPrefix = tokenPrefix,
            IsActive = existingToken.IsActive,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = existingToken.LastUsedAt,
            PlainTextToken = rawSecret
        };
    }
}
