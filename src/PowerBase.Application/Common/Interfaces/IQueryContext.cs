namespace PowerBase.Application.Common.Interfaces;

public interface IQueryContext
{
    long UserId { get; }
    long TenantId { get; }
    bool IsSuperAdmin { get; }
    string UserName { get; }
    string UserEmail { get; }
    string IpAddress { get; }
    IReadOnlySet<string> Permissions { get; }
    string TenantRole { get; }
    bool IsTenantAdmin { get; }
    void SetTenantId(long tenantId);
}
