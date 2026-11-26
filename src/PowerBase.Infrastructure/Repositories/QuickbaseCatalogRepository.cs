using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>meta.QuickbaseUserToken / meta.QuickbaseAppCatalog (tenant migration 099).</summary>
public class QuickbaseCatalogRepository : TenantRepositoryBase, IQuickbaseCatalogRepository
{
    public QuickbaseCatalogRepository(ITenantConnectionFactory tenantConnectionFactory, IQueryContext queryContext)
        : base(tenantConnectionFactory, queryContext) { }

    private const string TokenColumns = "Id, Name, Token, SortOrder, IsActive, CreatedOn, ModifiedOn";

    private const string AppSelect = """
        SELECT a.Id, a.QbAppId, a.LogicalId, a.Solution, a.UserTokenId, t.Name AS UserTokenName, a.CreatedOn, a.ModifiedOn
        FROM meta.QuickbaseAppCatalog a
        LEFT JOIN meta.QuickbaseUserToken t ON t.Id = a.UserTokenId
        """;

    // ---- tokens ----

    public async Task<IReadOnlyList<QuickbaseUserTokenRow>> ListTokensAsync(CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return (await c.QueryAsync<QuickbaseUserTokenRow>(new CommandDefinition(
            $"SELECT {TokenColumns} FROM meta.QuickbaseUserToken ORDER BY SortOrder, Name", cancellationToken: ct))).ToList();
    }

    public async Task<QuickbaseUserTokenRow?> GetTokenAsync(long id, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QuerySingleOrDefaultAsync<QuickbaseUserTokenRow>(new CommandDefinition(
            $"SELECT {TokenColumns} FROM meta.QuickbaseUserToken WHERE Id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<long> CreateTokenAsync(string name, string token, int sortOrder, bool isActive, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO meta.QuickbaseUserToken (Name, Token, SortOrder, IsActive, CreatedBy)
            OUTPUT INSERTED.Id
            VALUES (@name, @token, @sortOrder, @isActive, @userId)
            """, new { name, token, sortOrder, isActive, userId = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<bool> UpdateTokenAsync(long id, string name, string? token, int sortOrder, bool isActive, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.QuickbaseUserToken SET
                Name = @name, Token = COALESCE(@token, Token), SortOrder = @sortOrder, IsActive = @isActive,
                ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @userId
            WHERE Id = @id
            """, new { id, name, token, sortOrder, isActive, userId = QueryContext.UserId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteTokenAsync(long id, CancellationToken ct = default)
    {
        // Apps that used this token keep their row; the FK sets their token to NULL (every token is then tried).
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM meta.QuickbaseUserToken WHERE Id = @id", new { id }, cancellationToken: ct)) > 0;
    }

    // ---- apps ----

    public async Task<IReadOnlyList<QuickbaseCatalogAppRow>> ListAppsAsync(CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return (await c.QueryAsync<QuickbaseCatalogAppRow>(new CommandDefinition($"{AppSelect} ORDER BY a.Solution, a.LogicalId", cancellationToken: ct))).ToList();
    }

    public async Task<QuickbaseCatalogAppRow?> GetAppAsync(long id, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QuerySingleOrDefaultAsync<QuickbaseCatalogAppRow>(new CommandDefinition($"{AppSelect} WHERE a.Id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<QuickbaseCatalogAppRow?> FindAppByLogicalIdAsync(string logicalId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QueryFirstOrDefaultAsync<QuickbaseCatalogAppRow>(new CommandDefinition(
            $"{AppSelect} WHERE UPPER(a.LogicalId) = UPPER(@logicalId)", new { logicalId = logicalId.Trim() }, cancellationToken: ct));
    }

    public async Task<QuickbaseCatalogAppRow?> FindAppByQbAppIdAsync(string qbAppId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QueryFirstOrDefaultAsync<QuickbaseCatalogAppRow>(new CommandDefinition(
            $"{AppSelect} WHERE UPPER(a.QbAppId) = UPPER(@qbAppId)", new { qbAppId = qbAppId.Trim() }, cancellationToken: ct));
    }

    public async Task<long> CreateAppAsync(string qbAppId, string logicalId, string? solution, long? userTokenId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO meta.QuickbaseAppCatalog (QbAppId, LogicalId, Solution, UserTokenId, CreatedBy)
            OUTPUT INSERTED.Id
            VALUES (@qbAppId, @logicalId, @solution, @userTokenId, @userId)
            """, new { qbAppId, logicalId, solution, userTokenId, userId = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<bool> UpdateAppAsync(long id, string qbAppId, string logicalId, string? solution, long? userTokenId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.QuickbaseAppCatalog SET
                QbAppId = @qbAppId, LogicalId = @logicalId, Solution = @solution, UserTokenId = @userTokenId,
                ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @userId
            WHERE Id = @id
            """, new { id, qbAppId, logicalId, solution, userTokenId, userId = QueryContext.UserId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteAppAsync(long id, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM meta.QuickbaseAppCatalog WHERE Id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}
