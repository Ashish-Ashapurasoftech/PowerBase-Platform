using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
    }

    public SqlConnection Create() => new(_connectionString);
}
