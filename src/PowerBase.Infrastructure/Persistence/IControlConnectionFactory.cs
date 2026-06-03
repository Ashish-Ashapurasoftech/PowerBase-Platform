using Microsoft.Data.SqlClient;

namespace PowerBase.Infrastructure.Persistence;

public interface IControlConnectionFactory
{
    string ConnectionString { get; }
    SqlConnection Create();
}
