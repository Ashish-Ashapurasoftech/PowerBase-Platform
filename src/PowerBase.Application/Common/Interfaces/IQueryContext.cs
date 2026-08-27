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

    /// <summary>
    /// Applies user-token scope to this context. Used by JwtMiddleware for inbound
    /// pb_ut_* requests, and by saved PowerFlows accounts when opening a target-tenant
    /// scope so the token's app restrictions travel with the scope instead of being lost.
    /// </summary>
    void SetTokenScope(bool isUserToken, bool accessAllApps, IReadOnlySet<long> allowedAppIds);
    void SetUserIdentity(long userId, bool isSuperAdmin, string userName, string userEmail, IReadOnlySet<string> permissions, string tenantRole);
}
