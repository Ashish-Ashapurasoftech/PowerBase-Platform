using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Infrastructure.Services;

public class QueryContext : IQueryContext
{
    public long UserId { get; set; }
    public long TenantId { get; set; }
    public bool IsSuperAdmin { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public bool IsPipelineExecution { get; set; }
    public int PipelineDepth { get; set; } = 1;
    public string? PipelineChainJson { get; set; }
    public string TenantRole { get; set; } = string.Empty;
    public bool IsTenantAdmin => TenantRole == DefaultTenantRoles.Administrator;
    public bool IsUserToken { get; set; }
    public bool TokenAccessAllApps { get; set; } = true;
    public IReadOnlySet<long> AllowedAppIds { get; set; } = new HashSet<long>();

    public void SetTenantId(long tenantId) => TenantId = tenantId;

    public void SetUserIdentity(long userId, bool isSuperAdmin, string userName, string userEmail, IReadOnlySet<string> permissions, string tenantRole)
    {
        UserId      = userId;
        IsSuperAdmin = isSuperAdmin;
        UserName    = userName;
        UserEmail   = userEmail;
        Permissions = permissions;
        TenantRole  = tenantRole;
    }
}
