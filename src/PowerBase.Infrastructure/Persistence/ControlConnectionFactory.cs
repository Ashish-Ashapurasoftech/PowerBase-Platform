using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

public class ControlConnectionFactory : IControlConnectionFactory
{
    private readonly string _connectionString;

    public ControlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
    }

    public string ConnectionString => _connectionString;
    public SqlConnection Create() => new(_connectionString);
}
