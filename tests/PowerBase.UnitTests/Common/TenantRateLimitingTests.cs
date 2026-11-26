using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Middleware;
using PowerBase.Application.Common.Interfaces;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>API-01: one tenant cannot saturate an instance; health checks are never limited; off unless switched on.</summary>
public class TenantRateLimitingTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantRateLimiting(config);
        return services.BuildServiceProvider();
    }

    private static HttpContext Request(string path, long tenantId, string ip = "10.0.0.1")
    {
        var qc = Substitute.For<IQueryContext>();
        qc.TenantId.Returns(tenantId);
        var sp = new ServiceCollection().AddSingleton(qc).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    private static PartitionedRateLimiter<HttpContext> Limiter(ServiceProvider sp) =>
        sp.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

    private static Dictionary<string, string?> On(int tenantBurst = 3, int anon = 2) => new()
    {
        ["RateLimiting:Enabled"] = "true",
        ["RateLimiting:TenantBurst"] = tenantBurst.ToString(),
        ["RateLimiting:TenantRequestsPerSecond"] = "1",
        ["RateLimiting:AnonymousRequestsPerSecond"] = anon.ToString(),
        ["RateLimiting:QueueLimit"] = "0",
    };

    [Fact]
    public void Off_by_default_registers_no_limiter()
    {
        using var sp = Build(new());
        sp.GetRequiredService<TenantRateLimitOptions>().Enabled.Should().BeFalse();
        sp.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter.Should().BeNull();
    }

    [Fact]
    public void A_tenant_over_its_burst_is_refused_while_another_tenant_is_not()
    {
        using var sp = Build(On(tenantBurst: 3));
        var limiter = Limiter(sp);
        var granted = Enumerable.Range(0, 5).Count(_ => limiter.AttemptAcquire(Request("/apps", 7)).IsAcquired);
        granted.Should().Be(3);
        limiter.AttemptAcquire(Request("/apps", 8)).IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void Calls_without_a_tenant_are_limited_per_ip()
    {
        using var sp = Build(On(anon: 2));
        var limiter = Limiter(sp);
        Enumerable.Range(0, 4).Count(_ => limiter.AttemptAcquire(Request("/auth/login", 0)).IsAcquired).Should().Be(2);
        limiter.AttemptAcquire(Request("/auth/login", 0, "10.0.0.2")).IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void Health_endpoints_are_never_limited()
    {
        using var sp = Build(On(tenantBurst: 1, anon: 1));
        var limiter = Limiter(sp);
        Enumerable.Range(0, 50).All(_ => limiter.AttemptAcquire(Request("/health/ready", 0)).IsAcquired).Should().BeTrue();
    }

    [Fact]
    public void Refused_requests_get_429()
    {
        using var sp = Build(On());
        sp.GetRequiredService<IOptions<RateLimiterOptions>>().Value.RejectionStatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }
}
