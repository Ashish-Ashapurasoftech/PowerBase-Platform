using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserRepository : BaseRepository, IUserRepository
{
    private const string SelectColumns = "Id, PublicId, Email, PasswordHash, FirstName, LastName, SystemRoleId, IsActive, IsDeleted, CreatedAt, UpdatedAt, RowVersion";

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
        INSERT INTO core.[User] (Email, PasswordHash, FirstName, LastName, SystemRoleId, IsActive, IsDeleted, CreatedAt, UpdatedAt)
        OUTPUT INSERTED.Id
        VALUES (@email, @passwordHash, @firstName, @lastName, @systemRoleId, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME())
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
                    passwordHash = user.PasswordHash,
                    firstName = user.FirstName,
                    lastName = user.LastName,
                    systemRoleId = user.SystemRoleId,
                }, transaction, cancellationToken: ct));
        }
        finally
        {
            if (ownConnection) connection.Dispose();
        }
    }

    private async Task<IDbConnection> OpenNewConnectionAsync(CancellationToken ct)
    {
        var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        return conn;
    }
}
