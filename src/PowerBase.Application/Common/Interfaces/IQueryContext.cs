namespace PowerBase.Application.Common.Interfaces;

public interface IQueryContext
{
    long UserId { get; }
    long TenantId { get; }
    string IpAddress { get; }
    IReadOnlySet<string> Permissions { get; }
}
