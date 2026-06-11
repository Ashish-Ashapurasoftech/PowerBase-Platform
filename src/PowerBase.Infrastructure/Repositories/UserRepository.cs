using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserRepository : ControlRepositoryBase, IUserRepository
{
    private const string SelectColumns = "u.Id, u.PublicId, u.Email, u.EmailNormalized, u.HashedPassword, u.Name, u.SystemRoleId, sr.Code AS SystemRoleCode, u.IsEmailVerified, u.IsActive, u.IsDeleted, u.LastLoginOn, u.CreatedOn, u.CreatedBy, u.ModifiedOn, u.ModifiedBy, u.DeletedOn, u.DeletedBy, u.RowVersion";

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
        INSERT INTO core.[User] (Email, EmailNormalized, HashedPassword, Name, IsEmailVerified, IsActive, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id
        VALUES (@email, @emailNormalized, @hashedPassword, @name, 0, @isActive, 0, SYSUTCDATETIME(), 0)
        """;

    private const string ActivateSql = """
        UPDATE core.[User]
        SET Name = @name, HashedPassword = @hashedPassword, IsActive = 1, ModifiedOn = SYSUTCDATETIME()
        WHERE Id = @userId AND IsActive = 0 AND IsDeleted = 0
        """;

    public UserRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

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
                    name = user.Name,
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
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync<(long Id, string Name)>(
            new CommandDefinition(
                "SELECT Id, Name FROM core.[User] WHERE Id IN @ids AND IsDeleted = 0",
                new { ids = idList }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task ActivateAsync(long userId, string name, string hashedPassword, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ActivateSql, new { userId, name, hashedPassword }, cancellationToken: ct));
    }

}
