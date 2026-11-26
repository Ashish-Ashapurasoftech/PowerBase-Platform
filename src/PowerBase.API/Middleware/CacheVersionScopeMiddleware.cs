using PowerBase.Application.Common.Caching;

namespace PowerBase.API.Middleware;

/// <summary>
/// Opens a <see cref="CacheVersionScope"/> for the request: the metadata cache reads each tenant's shared versions once, at the
/// request's first cache use, so a permission or definition change committed on any instance before this request started is honoured
/// by it (ARC-01). Must run before anything that reads cached permissions (JwtMiddleware).
/// </summary>
public sealed class CacheVersionScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = CacheVersionScope.Begin();
        await next(context);
    }
}
