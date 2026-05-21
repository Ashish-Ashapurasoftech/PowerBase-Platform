using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserRepository : BaseRepository, IUserRepository
{
    private const string SelectColumns = "Id, PublicId, Email, EmailNormalized, HashedPassword, Name, IsEmailVerified, IsActive, IsDeleted, LastLoginOn, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string GetByEmailSql = $"""
        SELECT {SelectColumns}
        FROM core.[User]
        WHERE Email = @email
          AND IsDeleted = 0
        """;

    private const string GetByIdSql = $"""
        SELECT {SelectColumns}
        FROM core.[User]
        WHERE Id = @id
          AND IsDeleted = 0
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM core.[User]
        WHERE PublicId = @publicId
          AND IsDeleted = 0
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

    public UserRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
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

    public async Task ActivateAsync(long userId, string name, string hashedPassword, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(ActivateSql, new { userId, name, hashedPassword }, cancellationToken: ct));
    }

    private async Task<IDbConnection> OpenNewConnectionAsync(CancellationToken ct)
    {
        var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        return conn;
    }
}
