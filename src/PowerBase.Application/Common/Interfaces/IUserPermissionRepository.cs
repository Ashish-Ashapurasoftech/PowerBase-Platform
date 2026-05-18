namespace PowerBase.Application.Common.Interfaces;

public interface IUserPermissionRepository
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken ct = default);
}
