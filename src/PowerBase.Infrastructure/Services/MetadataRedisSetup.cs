using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Registers the optional Redis L2 of the metadata cache (docs 23 / 34) from <c>MetadataCache:Redis:ConnectionString</c> — local
/// development <c>localhost:6379</c>; Azure <c>{host}:10000,password={key from Key Vault},ssl=True,abortConnect=False</c>. Off when empty
/// or a "&lt;placeholder&gt;"; <see cref="MetadataCache"/> then works exactly as before (L1 + shared SQL versions).
/// Entra ID (no access key) is not wired yet: Microsoft.Azure.StackExchangeRedis brings an Azure.Identity that clashes with the
/// project's Azure.Core 1.60 (both define DefaultAzureCredential). See doc 34.
/// </summary>
public static class MetadataRedisSetup
{
    public static bool AddMetadataRedis(IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        var section = configuration.GetSection("MetadataCache:Redis");
        var connectionString = section["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.TrimStart().StartsWith('<')) return false;
        services.AddStackExchangeRedisCache(o =>
        {
            o.Configuration = connectionString;
            // The environment is in every key: UAT and production may share one Redis without ever reading each other's entries.
            o.InstanceName = section["InstanceName"] ?? $"powerbase:{environmentName.ToLowerInvariant()}:";
        });
        return true;
    }
}
