using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;

namespace PowerBase.Application.UserTokens.Queries.GetMyUserTokens;

public class GetMyUserTokensQuery
{
    public string? Search { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string SortBy { get; set; } = "createdAt";
    public bool SortDesc { get; set; } = true;
}

public class GetMyUserTokensResult
{
    public IEnumerable<UserTokenDto> Items { get; set; } = Enumerable.Empty<UserTokenDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
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

    public async Task<GetMyUserTokensResult> HandleAsync(GetMyUserTokensQuery query, CancellationToken cancellationToken = default)
    {
        var (tokens, totalCount) = await _userTokenRepository.GetMyTokensPagedAsync(
            _queryContext.UserId, 
            _queryContext.TenantId, 
            query.Search, 
            query.IsActive, 
            query.Page, 
            query.PageSize, 
            query.SortBy, 
            query.SortDesc, 
            cancellationToken);

        var resultList = new List<UserTokenDto>();

        foreach (var token in tokens)
        {
            var allowedApps = token.AccessAllApps 
                ? Enumerable.Empty<Guid>() 
                : await _userTokenRepository.GetAllowedAppPublicIdsAsync(token.Id, token.TenantId, cancellationToken);

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

        return new GetMyUserTokensResult
        {
            Items = resultList,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }
}
