using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>
/// The field type catalogue (core.FieldType in the tenant database). Seeded reference data that only migrations change, so the whole
/// catalogue is one cache entry per tenant (FieldTypes group, TTL as the backstop) and every lookup is answered from it.
/// </summary>
public class FieldTypeRepository : TenantRepositoryBase, IFieldTypeRepository
{
    private const string ListAllSql = """
        SELECT Id, Code, DisplayName, Category, SqlDataType, Icon
        FROM core.FieldType
        ORDER BY Id
        """;

    private readonly IMetadataCache _cache;

    public FieldTypeRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache cache)
        : base(connectionFactory, queryContext)
    {
        _cache = cache;
    }

    public async Task<FieldType?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        // Same match as "WHERE Code = @code" (the column's case-insensitive collation).
        var match = (await LoadAsync(ct)).FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : CopyOf(match);
    }

    public async Task<int> GetIdByCodeAsync(string code, CancellationToken ct = default) =>
        (await LoadAsync(ct)).FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;

    public async Task<IReadOnlyList<FieldType>> ListAllAsync(CancellationToken ct = default) =>
        (await LoadAsync(ct)).Select(CopyOf).ToList();

    private Task<IReadOnlyList<FieldType>> LoadAsync(CancellationToken ct) =>
        _cache.GetOrAddAsync<IReadOnlyList<FieldType>>(QueryContext.TenantId, MetadataCacheGroups.FieldTypes, "all", async () =>
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            var rows = await connection.QueryAsync<FieldType>(new CommandDefinition(ListAllSql, cancellationToken: ct));
            return rows.ToList();
        });

    private static FieldType CopyOf(FieldType t) => new()
    {
        Id = t.Id, Code = t.Code, DisplayName = t.DisplayName, Category = t.Category, SqlDataType = t.SqlDataType, Icon = t.Icon,
        SupportsDefault = t.SupportsDefault, SupportsRequired = t.SupportsRequired, SupportsUnique = t.SupportsUnique,
        DisplayOrder = t.DisplayOrder, IsActive = t.IsActive,
    };
}
