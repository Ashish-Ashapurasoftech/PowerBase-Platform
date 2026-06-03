using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public abstract class TenantRepositoryBase
{
    protected readonly ITenantConnectionFactory ConnectionFactory;
    protected readonly IQueryContext QueryContext;

    protected TenantRepositoryBase(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
    {
        ConnectionFactory = connectionFactory;
        QueryContext = queryContext;
    }

    protected async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        return conn;
    }
}
