using Microsoft.Data.SqlClient;

namespace PowerBase.Infrastructure.Persistence;

public interface ITenantConnectionFactory
{
    Task<SqlConnection> CreateAsync(CancellationToken ct = default);
}
