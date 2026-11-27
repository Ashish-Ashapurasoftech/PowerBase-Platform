using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Middleware;

public class QueryContextMiddleware
{
    private readonly RequestDelegate _next;

    public QueryContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, QueryContext queryContext)
    {
        queryContext.IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        // The app page this request came from (frontend page-context interceptor). Kept short; only a path is expected.
        var page = context.Request.Headers[PowerBase.Application.Common.PageContext.HeaderName].ToString();
        queryContext.ClientPage = string.IsNullOrWhiteSpace(page) ? null : (page.Length > 500 ? page[..500] : page);
        // Reads only: per-table "View" guards loading an item's definition (PageContext).
        queryContext.ApiReadPath = HttpMethods.IsGet(context.Request.Method) ? context.Request.Path.Value : null;
        await _next(context);
    }
}
