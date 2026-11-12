using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.QbMigration;
using PowerBase.Infrastructure.Services;

namespace PowerBase.QbMigration.Worker;

/// <summary>Builds the worker's objects by hand: only what the migration needs, reusing PowerBase's tenant routing and
/// encryption classes directly (no API service graph, no hosted workers, no pipeline triggers).</summary>
public sealed class Composition : IDisposable
{
    public IConfiguration Config { get; }
    public ILoggerFactory Logs { get; }
    public long TenantId { get; }
    public ITenantConnectionFactory Tenant { get; }
    public Func<SqlConnection> Control { get; }
    public IEncryptionService Encryption { get; }

    public Composition(IConfiguration config, long tenantId)
    {
        Config = config;
        TenantId = tenantId;
        Logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));

        var controlFactory = new ControlConnectionFactory(config);
        Control = controlFactory.Create;
        ISecretResolver secrets = string.IsNullOrEmpty(config["KeyVault:Uri"]) ? new ConfigSecretResolver(config) : new KeyVaultSecretResolver(config);
        var resolver = new TenantConnectionResolver(controlFactory, secrets, new MemoryCache(new MemoryCacheOptions()));
        Tenant = new FixedTenantConnectionFactory(resolver, tenantId);
        Encryption = new CachedDekEncryptionService(config, new AesEncryptionService(config));
    }

    public QuickBaseHttpClient QuickBase()
    {
        var options = new QuickBaseClientOptions
        {
            RealmHostname = Config["QuickBase:RealmHostname"] ?? "",
            UserToken = Config["QuickBase:UserToken"] ?? "",
            RequestsPerSecond = Config.GetValue("QuickBase:RequestsPerSecond", 8.0),
            RequestTimeout = TimeSpan.FromSeconds(Config.GetValue("QuickBase:RequestTimeoutSeconds", 60)),
        };
        // The client applies its own per-request timeout and retry rules; HttpClient's own 100 s timeout must not cut in first.
        var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        return new QuickBaseHttpClient(http, options, new QbRateLimiter(options.RequestsPerSecond), Logs.CreateLogger<QuickBaseHttpClient>());
    }

    public QbmStateStore State() => new(Tenant);
    public QbmPowerBaseReader PowerBase() => new(Tenant, Control);
    public QbmChunkLoader Loader() => new(Tenant, Encryption);

    public void Dispose() => Logs.Dispose();

    /// <summary>Connections to the one tenant being migrated.</summary>
    private sealed class FixedTenantConnectionFactory(ITenantConnectionResolver resolver, long tenantId) : ITenantConnectionFactory
    {
        public async Task<SqlConnection> CreateAsync(CancellationToken ct = default) => new(await resolver.ResolveAsync(tenantId, ct));
    }
}
