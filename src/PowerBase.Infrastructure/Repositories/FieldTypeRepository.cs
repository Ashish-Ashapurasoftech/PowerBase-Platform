using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class FieldTypeRepository : TenantRepositoryBase, IFieldTypeRepository
{
    private const string GetByCodeSql = """
        SELECT Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive
        FROM core.FieldType
        WHERE Code = @code AND IsActive = 1
        """;

    public FieldTypeRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<FieldType?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<FieldType>(
            new CommandDefinition(GetByCodeSql, new { code }, cancellationToken: ct));
    }

    public async Task<int> GetIdByCodeAsync(string code, CancellationToken ct = default)
    {
        const string sql = "SELECT Id FROM core.FieldType WHERE Code = @code AND IsActive = 1";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { code }, cancellationToken: ct));
    }
}
