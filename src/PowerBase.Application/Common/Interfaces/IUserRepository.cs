using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User> GetByIdAsync(long id, CancellationToken ct = default);
    Task<User> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, string>> GetNamesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task<long> CreateAsync(User user, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task ActivateAsync(long userId, string firstName, string lastName, string hashedPassword, CancellationToken ct = default);
    Task UpdateProfileAsync(long userId, string firstName, string lastName, CancellationToken ct = default);
    Task UpdatePasswordAsync(long userId, string hashedPassword, CancellationToken ct = default);
    Task UpdatePreferencesAsync(long userId, string? preferences, CancellationToken ct = default);
}
