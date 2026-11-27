namespace PowerBase.Application.Common.Interfaces;

public interface IQueryContext
{
    long UserId { get; }
    long TenantId { get; }
    bool IsSuperAdmin { get; }
    string UserName { get; }
    string UserEmail { get; }
    string IpAddress { get; }
    /// <summary>The app page (route path) the browser made this request from — sent by the PowerBase frontend in
    /// <c>PageContext.HeaderName</c>. Null for calls that don't come from the app (integrations, jobs). Used to apply
    /// per-table "View" builder permissions only on builder screens (see AppAccessService).</summary>
    string? ClientPage => null;
    /// <summary>The API request path (e.g. /reports/{id}) of a GET request; null otherwise or outside a request.</summary>
    string? ApiReadPath => null;
    IReadOnlySet<string> Permissions { get; }
    bool IsPipelineExecution { get; set; }
    int PipelineDepth { get; set; }
    string? PipelineChainJson { get; set; }
    string TenantRole { get; }
    bool IsTenantAdmin { get; }
    bool IsUserToken { get; }
    /// <summary>The `jti` of the JWT this request authenticated with; null for user tokens / anonymous.</summary>
    Guid? JwtId { get; }
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
