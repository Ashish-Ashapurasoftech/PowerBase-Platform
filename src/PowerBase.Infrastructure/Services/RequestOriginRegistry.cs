using System.Collections.Concurrent;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>In-memory, per tenant: the latest address a signed-in request reached this API at.</summary>
public sealed class RequestOriginRegistry : IRequestOriginRegistry
{
    private readonly ConcurrentDictionary<long, string> _origins = new();

    public void Remember(long tenantId, string origin)
    {
        if (tenantId <= 0 || string.IsNullOrWhiteSpace(origin)) return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        _origins[tenantId] = origin.TrimEnd('/');
    }

    public string? Get(long tenantId) => _origins.TryGetValue(tenantId, out var origin) ? origin : null;
}
