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
    bool IsPipelineExecution { get; set; }
    int PipelineDepth { get; set; }
    string? PipelineChainJson { get; set; }
    string TenantRole { get; }
    bool IsTenantAdmin { get; }
    bool IsUserToken { get; }
    bool TokenAccessAllApps { get; }
    IReadOnlySet<long> AllowedAppIds { get; }
    void SetTenantId(long tenantId);
    void SetUserIdentity(long userId, bool isSuperAdmin, string userName, string userEmail, IReadOnlySet<string> permissions, string tenantRole);
}
