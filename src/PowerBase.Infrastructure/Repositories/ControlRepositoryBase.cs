using System.Data;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public abstract class ControlRepositoryBase
{
    protected readonly IControlConnectionFactory ConnectionFactory;
    protected readonly IQueryContext QueryContext;

    protected ControlRepositoryBase(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
    {
        ConnectionFactory = connectionFactory;
        QueryContext = queryContext;
    }

    protected async Task<SqlConnection> OpenNewConnectionAsync(CancellationToken ct)
    {
        var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        return conn;
    }
}
