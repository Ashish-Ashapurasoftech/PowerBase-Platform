using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public sealed record UserIdentity(string Name, string Email);

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User> GetByIdAsync(long id, CancellationToken ct = default);
    /// <summary>
    /// The user as the auth middleware needs it on every API-token request (id, public id, name, email, system role, active, deleted),
    /// from the metadata cache. Never carries the password hash or preferences. Null when the user does not exist.
    /// </summary>
    Task<User?> GetSignInIdentityAsync(long id, CancellationToken ct = default);
    Task<User> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, string>> GetNamesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    /// <summary>Display name and email by user id, for the ids that exist (deleted users are omitted).</summary>
    Task<IReadOnlyDictionary<long, UserIdentity>> GetIdentitiesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, Guid>> GetPublicIdsByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task<long> CreateAsync(User user, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task ActivateAsync(long userId, string firstName, string lastName, string hashedPassword, CancellationToken ct = default);
    Task UpdateProfileAsync(long userId, string firstName, string lastName, CancellationToken ct = default);
    Task UpdatePasswordAsync(long userId, string hashedPassword, CancellationToken ct = default);
    Task UpdatePreferencesAsync(long userId, string? preferences, CancellationToken ct = default);
}
