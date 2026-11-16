using System.Xml.Linq;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// Quickbase XML calls carry credentials as request parameters (<c>usertoken</c> or <c>ticket</c>), in the URL or in the
/// <c>&lt;qdbapi&gt;</c> body. For <c>/db/*</c> requests without an Authorization header this copies that credential into
/// the Authorization header, so the existing JwtMiddleware authenticates it exactly as any other request
/// (user tokens: <c>pb_ut_...</c>; tickets: our JWT). Registered right before JwtMiddleware; other paths are untouched.
/// </summary>
public sealed class QbXmlTokenMiddleware
{
    private const int MAX_SCAN_BYTES = 64 * 1024 * 1024;
    private readonly RequestDelegate _next;
    public QbXmlTokenMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/db/", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(context.Request.Headers.Authorization))
        {
            var token = context.Request.Query["usertoken"].FirstOrDefault() ?? context.Request.Query["ticket"].FirstOrDefault();
            // Content-Length is absent for chunked uploads; read those too (up to the same limit).
            if (string.IsNullOrWhiteSpace(token) && context.Request.ContentLength is null or (> 0 and < MAX_SCAN_BYTES))
            {
                context.Request.EnableBuffering();
                var buffer = new char[MAX_SCAN_BYTES / 64];
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var sb = new System.Text.StringBuilder();
                int read;
                while (sb.Length < MAX_SCAN_BYTES && (read = await reader.ReadAsync(buffer, context.RequestAborted)) > 0) sb.Append(buffer, 0, read);
                context.Request.Body.Position = 0;
                token = ReadToken(sb.ToString());
            }
            if (!string.IsNullOrWhiteSpace(token))
                context.Request.Headers.Authorization = "QB-USER-TOKEN " + token.Trim();
        }
        await _next(context);
    }

    public static string? ReadToken(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var root = QbXmlRequest.SafeParse(body).Root;
            if (root is null) return null;
            string? Value(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            var t = Value("usertoken");
            return string.IsNullOrWhiteSpace(t) ? Value("ticket") : t;
        }
        catch (System.Xml.XmlException) { return null; }
    }
}
