using System.Diagnostics;
using Microsoft.Extensions.Options;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Middleware;

public class ServerTimingOptions
{
    public const string SectionName = "ServerTiming";

    /// <summary>Sends a Server-Timing header with the step timings of each request. The timings are only durations, no data.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Adds the Server-Timing header (plus Timing-Allow-Origin, so the browser exposes it to the SPA on another origin). The request's
/// own steps come from <see cref="ServerTiming"/>; <c>total</c> is the whole time spent in the API for the request.
/// </summary>
public class ServerTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;

    public ServerTimingMiddleware(RequestDelegate next, IOptions<ServerTimingOptions> options)
    {
        _next = next;
        _enabled = options.Value.Enabled;
    }

    public async Task InvokeAsync(HttpContext context, ServerTiming timing)
    {
        if (!_enabled)
        {
            await _next(context);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        // Started here (the outermost middleware), so the callback below holds the same instance the inner layers count into.
        var cache = PowerBase.Application.Common.Caching.CacheRequestStats.Begin();
        context.Response.OnStarting(() =>
        {
            AddCacheEntries(timing, cache);
            timing.Add("total", Stopwatch.GetElapsedTime(start));
            var value = timing.ToHeaderValue();
            if (value.Length > 0)
            {
                context.Response.Headers["Server-Timing"] = value;
                context.Response.Headers["Timing-Allow-Origin"] = "*";
            }
            return Task.CompletedTask;
        });
        await _next(context);
    }

    /// <summary>
    /// What the metadata cache cost this request: <c>cache-sync</c> (shared-version reads from SQL, desc = how many), <c>cache-l1</c>
    /// (in-memory hits, dur 0), <c>cache-l2</c> (Redis reads, desc = hits/misses), <c>cache-sql</c> (misses answered by SQL, desc =
    /// how many; bypassed ones included). Only what happened is listed.
    /// </summary>
    internal static void AddCacheEntries(ServerTiming timing, PowerBase.Application.Common.Caching.CacheRequestStats cache)
    {
        if (cache.SyncCount > 0) timing.Add("cache-sync", cache.SyncTime, cache.SyncCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (cache.L1Hits > 0) timing.Add("cache-l1", TimeSpan.Zero, cache.L1Hits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (cache.L2Hits + cache.L2Misses > 0 || cache.L2Time > TimeSpan.Zero)
            timing.Add("cache-l2", cache.L2Time, $"{cache.L2Hits}hit/{cache.L2Misses}miss");
        if (cache.SqlCount > 0)
            timing.Add("cache-sql", cache.SqlTime, cache.BypassCount > 0 ? $"{cache.SqlCount}({cache.BypassCount}bypass)" : cache.SqlCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Every SQL connection the request opened (cached reads and everything else): dur 0, the count is in desc.
        if (cache.TenantConnections + cache.ControlConnections > 0)
            timing.Add("db-conn", TimeSpan.Zero, $"{cache.TenantConnections}tenant/{cache.ControlConnections}control");
    }
}
