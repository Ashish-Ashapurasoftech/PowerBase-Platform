using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Middleware;

/// <summary>Remembers, per tenant, the address a signed-in request reached the API at (see <see cref="IRequestOriginRegistry"/>).</summary>
public class RequestOriginMiddleware
{
    private readonly RequestDelegate _next;

    public RequestOriginMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IQueryContext queryContext, IRequestOriginRegistry origins)
    {
        if (queryContext.TenantId > 0 && context.Request.Host.HasValue)
            origins.Remember(queryContext.TenantId, $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}");
        await _next(context);
    }
}
