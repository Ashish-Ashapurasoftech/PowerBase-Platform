using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class QueryContext : IQueryContext
{
    public long UserId { get; set; }
    public long TenantId { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
}
