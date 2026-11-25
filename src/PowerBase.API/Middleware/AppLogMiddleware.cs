using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Controllers;
using PowerBase.Application.AppLogs;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.AppLogs;

namespace PowerBase.API.Middleware;

/// <summary>
/// The application log (logs database, App Settings > Application Audit): every POST / PUT / PATCH / DELETE a signed-in
/// user of a tenant makes is written there, with who, when, the URL, the result and the body sent (secrets masked).
/// What the request changed comes from the audit calls the handlers already make (forwarded by AuditRepository to the
/// scoped <see cref="AppLogCollector"/>): one row per change, with its app, table, Record ID#, description and — for
/// records — the old / new value of every changed field. A request that reported no change still gets one row of its own.
/// A failed request (4xx / 5xx) is logged as one row with its status; the changes it reported are dropped, since its
/// transaction rolled back. Rows go to a background writer, so a slow or missing logs database never slows a request.
/// </summary>
public sealed partial class AppLogMiddleware
{
    private const int MaxStoredBody = 64 * 1024;
    private const long MaxReadBody = 1024 * 1024;
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private readonly RequestDelegate _next;
    private readonly ILogger<AppLogMiddleware> _logger;

    public AppLogMiddleware(RequestDelegate next, ILogger<AppLogMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AppLogCollector collector, IQueryContext who, IAppLogQueue queue)
    {
        if (!WriteMethods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        collector.BeginRequest();
        var body = await ReadBodyAsync(context.Request);
        var started = Stopwatch.GetTimestamp();
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await _next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            try
            {
                await WriteAsync(context, collector, who, queue, body, status, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Application log: could not log {Method} {Path}", context.Request.Method, context.Request.Path);
            }
        }
    }

    private static async Task WriteAsync(
        HttpContext context, AppLogCollector collector, IQueryContext who, IAppLogQueue queue, string? body, int status, int durationMs)
    {
        var events = collector.TakeEvents();
        // The application log is per tenant: requests made before a tenant is chosen (sign-in, tenant pick) are not app changes.
        if (who.TenantId <= 0) return;

        var request = context.Request;
        var path = request.Path + request.QueryString;
        var userAgent = request.Headers.UserAgent.ToString();

        void Stamp(AppLogEntry entry, bool withBody)
        {
            entry.HttpMethod = request.Method;
            entry.Path = path;
            entry.StatusCode = status;
            entry.DurationMs = durationMs;
            entry.UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent;
            entry.IpAddress ??= context.Connection.RemoteIpAddress?.ToString();
            if (withBody) entry.RequestBody = body;
        }

        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();

        if (status < 400 && events.Count > 0)
        {
            // Some audit calls name the table but not the app (forms, form rules…): the row still belongs to that app.
            var appOfTable = new Dictionary<long, long?>();
            (long? AppId, long? TableId)? routeScope = null;
            for (var i = 0; i < events.Count; i++)
            {
                var entry = AppLogCollector.ToEntry(events[i], who);
                // An app-level change (rename, import…) names the app only by its public id.
                if (entry.AppId is null && entry.EntityType == "App" && Guid.TryParse(entry.EntityId, out var eventAppPublicId))
                    entry.AppId = await AppIdAsync(context, eventAppPublicId);
                if (entry.AppId is null)
                {
                    if (entry.TableId is long eventTableId)
                    {
                        if (!appOfTable.TryGetValue(eventTableId, out var tableAppId))
                            appOfTable[eventTableId] = tableAppId = await AppOfTableAsync(context, eventTableId);
                        entry.AppId = tableAppId;
                    }
                    else
                    {
                        routeScope ??= await ResolveScopeAsync(context, []);
                        (entry.AppId, entry.TableId) = routeScope.Value;
                    }
                }
                if (string.IsNullOrWhiteSpace(entry.Description) && action is not null)
                    entry.Description = $"{SplitWords(action.ActionName)} ({entry.EntityType})";
                Stamp(entry, withBody: i == 0); // the body once per request, not once per record of a bulk change
                queue.Enqueue(entry);
            }
            return;
        }

        // No change reported (or the request failed): one row for the request itself, placed in its app / table from the route.
        var (appId, tableId) = await ResolveScopeAsync(context, events);
        var own = new AppLogEntry
        {
            TenantId = who.TenantId,
            AppId = appId,
            TableId = tableId,
            UserId = who.UserId > 0 ? who.UserId : null,
            UserName = string.IsNullOrEmpty(who.UserName) ? null : who.UserName,
            UserEmail = string.IsNullOrEmpty(who.UserEmail) ? null : who.UserEmail,
            Action = status >= 400 ? "Failed" : request.Method.ToUpperInvariant() switch
            {
                "POST" => "Requested",
                "DELETE" => "Deleted",
                _ => "Updated",
            },
            EntityType = action?.ControllerName ?? "Request",
            Description = (action is null ? "" : SplitWords(action.ActionName) + " — ") + $"{request.Method} {request.Path}"
                + (status >= 400 ? $" (failed: {status})" : ""),
            IpAddress = string.IsNullOrEmpty(who.IpAddress) ? null : who.IpAddress,
            OccurredOn = DateTime.UtcNow,
        };
        Stamp(own, withBody: true);
        queue.Enqueue(own);
    }

    /// <summary>
    /// The app / table a request belongs to when its changes don't say: from its events, else from its URL — an explicit
    /// tableId / appId, or the public id of what the route is about (a table, app, form, report, PowerFlow or relationship).
    /// Account-level requests (tenant roles, users, groups, user tokens…) have none and stay off every app's log.
    /// </summary>
    private static async Task<(long? AppId, long? TableId)> ResolveScopeAsync(HttpContext context, IReadOnlyList<AppLogEvent> events)
    {
        if (events.FirstOrDefault(e => e.AppId.HasValue) is { } known) return (known.AppId, known.TableId);
        var route = context.GetRouteData().Values;
        var services = context.RequestServices;
        var ct = CancellationToken.None;
        Guid? RouteGuid(string key) => route.TryGetValue(key, out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : null;

        try
        {
            if (RouteGuid("tableId") is { } tableKey) return await TableScopeAsync(services.GetRequiredService<IAppTableRepository>().GetByPublicIdAsync(tableKey, ct));
            if ((RouteGuid("appId") ?? RouteGuid("appPublicId")) is { } appKey) return (await AppIdAsync(context, appKey), null);

            var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerName;
            var key = RouteGuid("publicId") ?? RouteGuid("relId") ?? RouteGuid("id");
            if (key is not { } id) return (null, null);
            switch (controller)
            {
                case "Tables":
                    return await TableScopeAsync(services.GetRequiredService<IAppTableRepository>().GetByPublicIdAsync(id, ct));
                case "Apps":
                    return (await AppIdAsync(context, id), null);
                case "Forms":
                {
                    var form = await services.GetRequiredService<IFormRepository>().GetByPublicIdAsync(id, ct);
                    return (await AppOfTableAsync(context, form.AppTableId), form.AppTableId);
                }
                case "Reports":
                {
                    var report = await services.GetRequiredService<IReportRepository>().GetByPublicIdAsync(id, ct);
                    return (await AppOfTableAsync(context, report.AppTableId), report.AppTableId);
                }
                case "Pipelines":
                    return ((await services.GetRequiredService<IPipelineRepository>().GetByPublicIdAsync(id, ct)).AppId, null);
                case "Relationships":
                    return ((await services.GetRequiredService<IRelationshipRepository>().GetByPublicIdAsync(id, ct))?.AppId, null);
            }
        }
        catch
        {
            // a deleted table / app / form…, or no tenant database: the row is still kept, just without an app
        }
        return (null, null);

        static async Task<(long? AppId, long? TableId)> TableScopeAsync(Task<PowerBase.Domain.Entities.AppTable> table)
        {
            var t = await table;
            return (t.AppId, t.Id);
        }
    }

    private static async Task<long?> AppIdAsync(HttpContext context, Guid appPublicId)
    {
        try
        {
            var appId = await context.RequestServices.GetRequiredService<IAppRepository>().GetIdByPublicIdAsync(appPublicId, CancellationToken.None);
            return appId > 0 ? appId : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<long?> AppOfTableAsync(HttpContext context, long tableId)
    {
        try
        {
            var table = await context.RequestServices.GetRequiredService<IAppTableRepository>().GetByIdAsync(tableId, CancellationToken.None);
            return table.AppId;
        }
        catch
        {
            return null; // a table deleted by this very request: the row is still kept
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        var contentType = request.ContentType ?? "";
        if (request.ContentLength is 0 || (request.ContentLength is null && !request.Body.CanSeek && contentType.Length == 0)) return null;
        if (contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase)) return "[file upload]";
        if (request.ContentLength > MaxReadBody) return $"[{request.ContentLength:N0} bytes, not stored]";
        if (!(contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
              || contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
              || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)))
            return contentType.Length == 0 ? null : $"[{contentType}]";

        request.EnableBuffering();
        string text;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            text = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var masked = Mask(text);
        return masked.Length <= MaxStoredBody ? masked : masked[..MaxStoredBody] + "…(truncated)";
    }

    /// <summary>JSON bodies with passwords, tokens, secrets and keys replaced by "***"; other text as sent.</summary>
    private static string Mask(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is null) return text;
            MaskNode(node);
            return node.ToJsonString();
        }
        catch
        {
            return SecretPairs().Replace(text, m => m.Groups[1].Value + "=***");
        }
    }

    private static void MaskNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SecretName().IsMatch(key)) obj[key] = "***";
                    else if (obj[key] is { } child) MaskNode(child);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) MaskNode(child);
                break;
        }
    }

    private static string SplitWords(string name) => Words().Replace(name, " $1").Trim();

    [GeneratedRegex("password|passwd|secret|token|apikey|api_key|authorization|credential|privatekey|private_key", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"((?:password|passwd|secret|token|apikey|api_key|authorization)[^=&]*)=[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPairs();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex Words();
}
