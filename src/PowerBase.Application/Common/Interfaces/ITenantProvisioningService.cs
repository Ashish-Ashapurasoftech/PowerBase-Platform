using PowerBase.Application.Tenants.Commands.CreateTenant;

namespace PowerBase.Application.Common.Interfaces;

public interface ITenantProvisioningService
{
    /// <summary>
    /// Creates a dedicated database for the given tenant, runs baseline migrations,
    /// and marks the tenant as Ready. Throws on failure (tenant row is left in Failed state).
    /// When <paramref name="serverConfig"/> is provided the database is provisioned on the
    /// tenant's own Azure SQL server; otherwise the shared control server is used.
    /// </summary>
    Task ProvisionAsync(long tenantId, TenantServerConfig? serverConfig = null, CancellationToken ct = default);
}
