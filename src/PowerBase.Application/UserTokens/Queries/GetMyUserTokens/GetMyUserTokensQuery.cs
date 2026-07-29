using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;

namespace PowerBase.Application.UserTokens.Queries.GetMyUserTokens;

public class GetMyUserTokensQuery
{
}

public class GetMyUserTokensQueryHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;

    public GetMyUserTokensQueryHandler(IUserTokenRepository userTokenRepository, IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<IEnumerable<UserTokenDto>> HandleAsync(GetMyUserTokensQuery query, CancellationToken cancellationToken = default)
    {
        var tokens = await _userTokenRepository.GetMyTokensAsync(_queryContext.UserId, _queryContext.TenantId, cancellationToken);
        var resultList = new List<UserTokenDto>();

        foreach (var token in tokens)
        {
            var allowedApps = token.AccessAllApps 
                ? Enumerable.Empty<Guid>() 
                : await _userTokenRepository.GetAllowedAppPublicIdsAsync(token.Id, cancellationToken);

            resultList.Add(new UserTokenDto
            {
                PublicId = token.PublicId,
                TokenName = token.TokenName,
                Description = token.Description,
                TokenPrefix = token.TokenPrefix,
                IsActive = token.IsActive,
                AccessAllApps = token.AccessAllApps,
                CreatedAt = token.CreatedAt,
                LastUsedAt = token.LastUsedAt,
                AllowedAppPublicIds = allowedApps
            });
        }

        return resultList;
    }
}
