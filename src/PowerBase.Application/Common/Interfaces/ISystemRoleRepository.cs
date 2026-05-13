namespace PowerBase.Application.Common.Interfaces;

public interface ISystemRoleRepository
{
    Task<long> GetIdByCodeAsync(string code, CancellationToken ct = default);
}
