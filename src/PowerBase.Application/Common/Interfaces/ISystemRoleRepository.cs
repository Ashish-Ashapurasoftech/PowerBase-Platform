namespace PowerBase.Application.Common.Interfaces;

public interface ISystemRoleRepository
{
    Task<int> GetIdByCodeAsync(string code, CancellationToken ct = default);
}
