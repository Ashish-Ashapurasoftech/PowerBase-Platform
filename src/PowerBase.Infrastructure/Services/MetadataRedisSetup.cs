using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Registers the optional Redis L2 (metadata cache, report result cache — docs 23 / 34). Off unless configured; when off everything
/// works exactly as before (L1 + shared SQL versions, no result cache). Two ways to connect:
/// <list type="bullet">
/// <item><c>MetadataCache:Redis:Host</c> — production, Azure Managed Redis: Entra ID with the app's managed identity, TLS, no access key
/// (doc 34 §3). <c>MetadataCache:Redis:ManagedIdentityClientId</c> picks a user-assigned identity; empty = system-assigned.</item>
/// <item><c>MetadataCache:Redis:ConnectionString</c> — local development (<c>localhost:6379</c>) or a key-based cache (key from Key Vault).</item>
/// </list>
/// The environment name is in every key, so UAT and production may share one Redis without ever reading each other's entries.
/// </summary>
public static class MetadataRedisSetup
{
    public static bool AddMetadataRedis(IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        var section = configuration.GetSection("MetadataCache:Redis");
        var host = section["Host"];
        var connectionString = section["ConnectionString"];
        var instanceName = section["InstanceName"] ?? $"powerbase:{environmentName.ToLowerInvariant()}:";

        if (IsSet(host))
        {
            var clientId = section["ManagedIdentityClientId"];
            services.AddStackExchangeRedisCache(o =>
            {
                o.InstanceName = instanceName;
                o.ConnectionMultiplexerFactory = async () =>
                {
                    var options = ConfigurationOptions.Parse(host!);
                    options.Ssl = true;
                    options.AbortOnConnectFail = false;
                    // Entra ID: the token is fetched and renewed by the library before it expires.
                    await options.ConfigureForAzureWithTokenCredentialAsync(new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ManagedIdentityClientId = IsSet(clientId) ? clientId : null,
                    }));
                    return await ConnectionMultiplexer.ConnectAsync(options);
                };
            });
            return true;
        }

        if (IsSet(connectionString))
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = connectionString;
                o.InstanceName = instanceName;
            });
            return true;
        }
        return false;
    }

    /// <summary>Empty and "&lt;placeholder&gt;" values mean "not configured".</summary>
    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('<');
}
