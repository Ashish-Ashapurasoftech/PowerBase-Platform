namespace PowerBase.Application.Common.Interfaces;

public interface ITenantProvisioningService
{
    /// <summary>
    /// Creates a dedicated database for the given tenant, runs baseline migrations,
    /// and marks the tenant as Ready. Throws on failure (tenant row is left in Failed state).
    /// </summary>
    Task ProvisionAsync(long tenantId, CancellationToken ct = default);
}
