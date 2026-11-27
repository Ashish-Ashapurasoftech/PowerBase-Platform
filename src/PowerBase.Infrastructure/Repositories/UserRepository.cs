using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserRepository : ControlRepositoryBase, IUserRepository
{
    private const string SelectColumns = "u.Id, u.PublicId, u.Email, u.EmailNormalized, u.HashedPassword, u.Name, u.FirstName, u.LastName, u.SystemRoleId, sr.Code AS SystemRoleCode, u.IsEmailVerified, u.IsActive, u.Preferences, u.IsDeleted, u.LastLoginOn, u.CreatedOn, u.CreatedBy, u.ModifiedOn, u.ModifiedBy, u.DeletedOn, u.DeletedBy, u.RowVersion";

    private const string GetByEmailSql = $"""
        SELECT {SelectColumns}
        FROM core.[User] u
        LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
        WHERE u.Email = @email
          AND u.IsDeleted = 0
        """;

    private const string GetByIdSql = $"""
        SELECT {SelectColumns}
        FROM core.[User] u
        LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
        WHERE u.Id = @id
          AND u.IsDeleted = 0
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM core.[User] u
        LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
        WHERE u.PublicId = @publicId
          AND u.IsDeleted = 0
        """;

    private const string InsertSql = """
        MERGE core.[User] AS target
        USING (VALUES (@email, @emailNormalized)) AS source (Email, EmailNormalized)
        ON target.Email = source.Email
        WHEN MATCHED AND target.IsDeleted = 1 THEN
            UPDATE SET IsDeleted = 0, DeletedOn = NULL, DeletedBy = NULL,
                       HashedPassword = @hashedPassword, Name = @name,
                       FirstName = @firstName, LastName = @lastName,
                       IsActive = @isActive, ModifiedOn = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (Email, EmailNormalized, HashedPassword, Name, FirstName, LastName, IsEmailVerified, IsActive, IsDeleted, CreatedOn, CreatedBy)
            VALUES (@email, @emailNormalized, @hashedPassword, @name, @firstName, @lastName, 0, @isActive, 0, SYSUTCDATETIME(), 0)
        OUTPUT INSERTED.Id;
        """;

    private const string ActivateSql = """
        UPDATE core.[User]
        SET FirstName = @firstName, LastName = @lastName, Name = @name, HashedPassword = @hashedPassword, IsActive = 1, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @userId AND IsActive = 0 AND IsDeleted = 0
        """;

    private const string UpdateProfileSql = """
        UPDATE core.[User]
        SET FirstName = @firstName, LastName = @lastName, Name = @name, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @userId AND IsDeleted = 0
        """;

    private const string UpdatePasswordSql = """
        UPDATE core.[User]
        SET HashedPassword = @hashedPassword, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @userId AND IsDeleted = 0
        """;

    private const string UpdatePreferencesSql = """
        UPDATE core.[User]
        SET Preferences = @preferences, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @userId AND IsDeleted = 0
        """;

    private const string GetSignInIdentitySql = """
        SELECT u.Id, u.PublicId, u.Email, u.Name, u.FirstName, u.LastName, u.SystemRoleId, sr.Code AS SystemRoleCode,
               u.IsEmailVerified, u.IsActive, u.IsDeleted
        FROM core.[User] u
        LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
        WHERE u.Id = @id
          AND u.IsDeleted = 0
        """;

    private readonly IMetadataCache _cache;

    public UserRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache cache)
        : base(connectionFactory, queryContext)
    {
        _cache = cache;
    }

    /// <summary>Drops the cached sign-in identities and public ids. Called BEFORE every write to core.[User] that changes them
    /// (here and in AdminRepository).</summary>
    private void UsersChanging() => _cache.Invalidate(MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.Users);

    public async Task<User?> GetSignInIdentityAsync(long id, CancellationToken ct = default)
    {
        // A missing user is not cached (null is never stored). The cached instance is shared: each caller gets a copy.
        var cached = await _cache.GetOrAddAsync<User?>(MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.Users, $"signin:{id}", async () =>
        {
            await using var connection = ConnectionFactory.Create();
            return await connection.QuerySingleOrDefaultAsync<User>(
                new CommandDefinition(GetSignInIdentitySql, new { id }, cancellationToken: ct));
        });
        return cached is null ? null : new User
        {
            Id = cached.Id, PublicId = cached.PublicId, Email = cached.Email, Name = cached.Name, FirstName = cached.FirstName,
            LastName = cached.LastName, SystemRoleId = cached.SystemRoleId, SystemRoleCode = cached.SystemRoleCode,
            IsEmailVerified = cached.IsEmailVerified, IsActive = cached.IsActive, IsDeleted = cached.IsDeleted,
        };
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(GetByEmailSql, new { email }, cancellationToken: ct));
    }

    public async Task<User> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var user = await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(GetByIdSql, new { id }, cancellationToken: ct));
        return user ?? throw new NotFoundException("User", id);
    }

    public async Task<User> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var user = await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return user ?? throw new NotFoundException("User", publicId);
    }

    public async Task<long> CreateAsync(User user, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        // The MERGE can revive a deleted user (same id, new name / active flag).
        _cache.Invalidate(MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.Users, inTransaction: transaction is not null);
        var connection = transaction?.Connection ?? (IDbConnection)(await OpenNewConnectionAsync(ct));
        bool ownConnection = transaction is null;
        try
        {
            return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(InsertSql, new
                {
                    email = user.Email,
                    emailNormalized = user.Email.ToUpperInvariant(),
                    hashedPassword = user.HashedPassword,
                    name = string.IsNullOrWhiteSpace(user.Name) ? $"{user.FirstName} {user.LastName}".Trim() : user.Name,
                    firstName = user.FirstName ?? string.Empty,
                    lastName = user.LastName ?? string.Empty,
                    isActive = user.IsActive,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    public async Task<IReadOnlyDictionary<long, string>> GetNamesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<long, string>();
        var parameters = new DynamicParameters();
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync<(long Id, string Name)>(
            new CommandDefinition(
                $"SELECT Id, Name FROM core.[User] WHERE {SqlInList.Predicate("Id", "ids", idList, parameters)} AND IsDeleted = 0",
                parameters, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task<IReadOnlyDictionary<long, UserIdentity>> GetIdentitiesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<long, UserIdentity>();
        var parameters = new DynamicParameters();
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync<(long Id, string Name, string Email)>(
            new CommandDefinition(
                $"SELECT Id, Name, Email FROM core.[User] WHERE {SqlInList.Predicate("Id", "ids", idList, parameters)} AND IsDeleted = 0",
                parameters, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => new UserIdentity(r.Name ?? string.Empty, r.Email ?? string.Empty));
    }

    public async Task<IReadOnlyDictionary<long, Guid>> GetPublicIdsByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<long, Guid>();
        if (idList.Count == 1)
        {
            // The current user's public id, asked on every app entry (GET /apps/{id}/permissions). A user's public id never changes;
            // the entry goes with the sign-in identity, so a deleted user drops out with it.
            var identity = await GetSignInIdentityAsync(idList[0], ct);
            return identity is null ? new Dictionary<long, Guid>() : new Dictionary<long, Guid> { [identity.Id] = identity.PublicId };
        }
        var parameters = new DynamicParameters();
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync<(long Id, Guid PublicId)>(
            new CommandDefinition(
                $"SELECT Id, PublicId FROM core.[User] WHERE {SqlInList.Predicate("Id", "ids", idList, parameters)} AND IsDeleted = 0",
                parameters, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.PublicId);
    }

    public async Task ActivateAsync(long userId, string firstName, string lastName, string hashedPassword, CancellationToken ct = default)
    {
        var name = $"{firstName} {lastName}".Trim();
        UsersChanging();
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ActivateSql, new { userId, firstName, lastName, name, hashedPassword }, cancellationToken: ct));
    }

    public async Task UpdateProfileAsync(long userId, string firstName, string lastName, CancellationToken ct = default)
    {
        var name = $"{firstName} {lastName}".Trim();
        UsersChanging();
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateProfileSql, new { userId, firstName, lastName, name }, cancellationToken: ct));
    }

    public async Task UpdatePasswordAsync(long userId, string hashedPassword, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePasswordSql, new { userId, hashedPassword }, cancellationToken: ct));
    }

    public async Task UpdatePreferencesAsync(long userId, string? preferences, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePreferencesSql, new { userId, preferences }, cancellationToken: ct));
    }

}
