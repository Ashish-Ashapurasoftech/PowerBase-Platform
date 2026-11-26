using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Middleware;

/// <summary>Settings of the per-tenant rate limit ("RateLimiting" section; API-01). Off unless Enabled.</summary>
public sealed class TenantRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Off by default: switch on per environment once the limits fit real traffic (watch the 429 count first).</summary>
    public bool Enabled { get; set; }

    /// <summary>Sustained requests per second per tenant (token bucket refill), across all of its users, per API instance.</summary>
    public int TenantRequestsPerSecond { get; set; } = 50;

    /// <summary>Burst a tenant may use above the sustained rate.</summary>
    public int TenantBurst { get; set; } = 200;

    /// <summary>Requests per second per client IP for calls with no tenant yet (sign-in, invites).</summary>
    public int AnonymousRequestsPerSecond { get; set; } = 10;

    /// <summary>Requests that may wait for a token before being refused with 429.</summary>
    public int QueueLimit { get; set; } = 20;
}

/// <summary>
/// One tenant (or one client without a tenant) cannot saturate an instance: a token bucket per tenant id, after JwtMiddleware has set
/// the tenant. Health endpoints are never limited. Refused requests get 429 with Retry-After.
/// </summary>
public static class TenantRateLimiting
{
    public static IServiceCollection AddTenantRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TenantRateLimitOptions.SectionName).Get<TenantRateLimitOptions>() ?? new TenantRateLimitOptions();
        services.AddSingleton(options);
        if (!options.Enabled) return services;

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.CompletedTask;
            };
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (http.Request.Path.StartsWithSegments("/health"))
                    return RateLimitPartition.GetNoLimiter("health");

                var tenantId = http.RequestServices.GetService<IQueryContext>()?.TenantId ?? 0;
                if (tenantId > 0)
                    return RateLimitPartition.GetTokenBucketLimiter($"t:{tenantId}", _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(1, options.TenantBurst),
                        TokensPerPeriod = Math.Max(1, options.TenantRequestsPerSecond),
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = Math.Max(0, options.QueueLimit),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    });

                var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"ip:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.AnonymousRequestsPerSecond),
                    Window = TimeSpan.FromSeconds(1),
                    QueueLimit = Math.Max(0, options.QueueLimit),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });
        return services;
    }

    public static IApplicationBuilder UseTenantRateLimiting(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<TenantRateLimitOptions>();
        return options.Enabled ? app.UseRateLimiter() : app;
    }
}
