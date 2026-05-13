using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User> GetByIdAsync(long id, CancellationToken ct = default);
    Task<User> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> CreateAsync(User user, IDbTransaction? transaction = null, CancellationToken ct = default);
}
