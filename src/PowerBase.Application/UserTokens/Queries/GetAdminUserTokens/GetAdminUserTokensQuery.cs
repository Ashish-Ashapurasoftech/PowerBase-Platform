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

        var resultList = new List<AdminUserTokenDto>();

        foreach (var token in tokens)
        {
            var allowedApps = token.AccessAllApps 
                ? Enumerable.Empty<Guid>() 
                : await _userTokenRepository.GetAllowedAppPublicIdsAsync(token.Id, cancellationToken);

            var owner = await _userRepository.GetByIdAsync(token.UserId, cancellationToken);

            // Display format: ctg8************yhbn (first 4 and last 4 chars)
            var tokenMasked = MaskTokenPrefix(token.TokenPrefix);

            resultList.Add(new AdminUserTokenDto
            {
                PublicId = token.PublicId,
                TokenName = token.TokenName,
                Description = token.Description,
                TokenPrefix = tokenMasked,
                IsActive = token.IsActive,
                AccessAllApps = token.AccessAllApps,
                CreatedAt = token.CreatedAt,
                LastUsedAt = token.LastUsedAt,
                AllowedAppPublicIds = allowedApps,
                UserId = token.UserId,
                OwnerName = owner?.Name ?? string.Empty,
                OwnerEmail = owner?.Email ?? string.Empty
            });
        }

        return new GetAdminUserTokensResult
        {
            Items = resultList,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    private static string MaskTokenPrefix(string prefix)
    {
        var cleanPrefix = prefix.Replace("...", "");
        if (cleanPrefix.Length >= 8)
        {
            var first4 = cleanPrefix.Substring(0, 4);
            var last4 = cleanPrefix.Substring(cleanPrefix.Length - 4, 4);
            return $"{first4}************{last4}";
        }
        return $"{prefix}************";
    }
}
