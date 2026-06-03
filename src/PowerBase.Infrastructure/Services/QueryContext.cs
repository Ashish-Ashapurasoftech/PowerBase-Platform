using PowerBase.Application.Common.Interfaces;

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
}
