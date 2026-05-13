using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Middleware;

public class QueryContextMiddleware
{
    private readonly RequestDelegate _next;

    public QueryContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, QueryContext queryContext)
    {
        queryContext.IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        await _next(context);
    }
}
