using PowerBase.Application.AppTokens.Common;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.AppTokens.Queries.GetAppTokens;

public class GetAppTokensQuery
{
    public Guid AppPublicId { get; set; }
    public string? Search { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class GetAppTokensResult
{
    public IEnumerable<AppTokenDto> Items { get; set; } = Enumerable.Empty<AppTokenDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class GetAppTokensQueryHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;

    public GetAppTokensQueryHandler(IAppTokenRepository appTokenRepository, IQueryContext queryContext)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
    }

    public async Task<GetAppTokensResult> HandleAsync(GetAppTokensQuery query, CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await _appTokenRepository.GetPagedAsync(
            _queryContext.TenantId, query.AppPublicId, query.Search, query.IsActive, query.Page, query.PageSize, cancellationToken);

        var dtos = items.Select(t => new AppTokenDto
        {
            PublicId = t.PublicId,
            AppPublicId = query.AppPublicId,
            CreatedByUserId = t.CreatedByUserId,
            TokenName = t.TokenName,
            Description = t.Description,
            TokenPrefix = t.TokenPrefix,
            IsActive = t.IsActive,
            CreatedAt = t.CreatedAt,
            LastUsedAt = t.LastUsedAt
        }).ToList();

        return new GetAppTokensResult
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }
}
