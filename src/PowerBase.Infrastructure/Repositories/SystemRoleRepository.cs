using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class SystemRoleRepository : BaseRepository, ISystemRoleRepository
{
    private const string GetIdByCodeSql = """
        SELECT Id FROM core.SystemRole WHERE Code = @code
        """;

    public SystemRoleRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<long> GetIdByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetIdByCodeSql, new { code }, cancellationToken: ct));
        return id ?? throw new NotFoundException("SystemRole", code);
    }
}
