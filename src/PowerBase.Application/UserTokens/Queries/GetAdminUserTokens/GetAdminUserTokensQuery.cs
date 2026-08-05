using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;

namespace PowerBase.Application.UserTokens.Queries.GetAdminUserTokens;

public class GetAdminUserTokensQuery
{
    public string? Search { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class GetAdminUserTokensResult
{
    public IEnumerable<AdminUserTokenDto> Items { get; set; } = Enumerable.Empty<AdminUserTokenDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class GetAdminUserTokensQueryHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IUserRepository _userRepository;
    private readonly IQueryContext _queryContext;

    public GetAdminUserTokensQueryHandler(
        IUserTokenRepository userTokenRepository, 
        IUserRepository userRepository, 
        IQueryContext queryContext)
    {
        _userTokenRepository = userTokenRepository;
        _userRepository = userRepository;
        _queryContext = queryContext;
    }

    public async Task<GetAdminUserTokensResult> HandleAsync(GetAdminUserTokensQuery query, CancellationToken cancellationToken = default)
    {
        var (tokens, totalCount) = await _userTokenRepository.GetAdminTokensPagedAsync(
            _queryContext.TenantId,
            query.Search,
            query.IsActive,
            query.Page,
            query.PageSize,
            cancellationToken
        );

        return new GetAdminUserTokensResult
        {
            Items = tokens,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }
}
