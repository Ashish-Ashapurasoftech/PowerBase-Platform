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
        context.Response.OnStarting(() =>
        {
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
}
