namespace PowerBase.Infrastructure.Persistence;

public interface ITenantConnectionResolver
{
    Task<string> ResolveAsync(long tenantId, CancellationToken ct = default);
    void Invalidate(long tenantId);
}
