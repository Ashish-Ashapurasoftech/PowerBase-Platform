using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;

namespace PowerBase.Application.UserTokens.Queries.GetSingleTokenDetail;

public class GetSingleTokenDetailQuery
{
    public Guid TokenId { get; set; }

    public GetSingleTokenDetailQuery(Guid tokenId)
    {
        TokenId = tokenId;
    }
}

public class GetSingleTokenDetailQueryHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IUserRepository _userRepository;
    private readonly IQueryContext _queryContext;

    public GetSingleTokenDetailQueryHandler(
        IUserTokenRepository userTokenRepository,
        IUserRepository userRepository,
        IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _userRepository = userRepository;
        _queryContext = queryContext;
    }

    public async Task<AdminUserTokenDto?> HandleAsync(GetSingleTokenDetailQuery query, CancellationToken cancellationToken = default)
    {
        var token = await _userTokenRepository.GetByPublicIdAsync(query.TokenId, _queryContext.TenantId, cancellationToken);
        if (token == null) return null;

        var allowedApps = token.AccessAllApps 
            ? Enumerable.Empty<Guid>() 
            : await _userTokenRepository.GetAllowedAppPublicIdsAsync(token.Id, token.TenantId, cancellationToken);

        var owner = await _userRepository.GetByIdAsync(token.UserId, cancellationToken);

        var first4 = token.TokenPrefix.Length >= 4 ? token.TokenPrefix.Substring(0, 4) : token.TokenPrefix;
        var maskedToken = $"{first4}************";

        return new AdminUserTokenDto
        {
            PublicId = token.PublicId,
            TokenName = token.TokenName,
            Description = token.Description,
            TokenPrefix = maskedToken,
            IsActive = token.IsActive,
            AccessAllApps = token.AccessAllApps,
            CreatedAt = token.CreatedAt,
            LastUsedAt = token.LastUsedAt,
            AllowedAppPublicIds = allowedApps,
            UserId = token.UserId,
            OwnerName = owner?.Name ?? string.Empty,
            OwnerEmail = owner?.Email ?? string.Empty
        };
    }
}
