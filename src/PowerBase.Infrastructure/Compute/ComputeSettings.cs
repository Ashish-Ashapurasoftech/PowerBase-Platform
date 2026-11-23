using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Compute is on for a tenant only when the global Compute:Enabled is true AND the tenant's own ComputeEnabled flag (control
/// meta.Tenant) is set. Turning either off restores today's behaviour: the outbox is no longer written and the worker skips the tenant.
/// The tenant flag is cached briefly so the check costs nothing on the hot save path.
/// </summary>
public sealed class ComputeSettings : IComputeSettings
{
    private const int InvalidColumnName = 207;

    private readonly IControlConnectionFactory _control;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComputeSettings>? _logger;

    public ComputeSettings(IControlConnectionFactory control, IMemoryCache cache, IConfiguration configuration, ILogger<ComputeSettings>? logger = null)
    {
        _control = control;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public bool StoreAllCalculatedFields => ComputeConfig.ReadCompute(_configuration).StoreAllCalculatedFields;

    public async Task<bool> IsComputeEnabledAsync(long tenantId, CancellationToken ct = default)
    {
        var options = ComputeConfig.ReadCompute(_configuration);
        if (!options.Enabled || tenantId <= 0) return false;

        var key = $"compute_enabled_{tenantId}";
        if (_cache.TryGetValue(key, out bool cached)) return cached;

        var enabled = false;
        try
        {
            await using var conn = _control.Create();
            await conn.OpenAsync(ct);
            enabled = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                "SELECT ComputeEnabled FROM meta.Tenant WHERE Id = @tenantId AND IsDeleted = 0", new { tenantId }, cancellationToken: ct)) ?? false;
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            // The control migration (018) has not run yet: the feature is off, as it was before it existed.
            enabled = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cannot tell: stay off and do not cache, so the next call asks again. A save never fails because of this check.
            _logger?.LogWarning(ex, "Compute: could not read the ComputeEnabled flag of tenant {TenantId}; treating it as off.", tenantId);
            return false;
        }

        _cache.Set(key, enabled, TimeSpan.FromSeconds(Math.Max(1, options.SettingsCacheSeconds)));
        return enabled;
    }
}
