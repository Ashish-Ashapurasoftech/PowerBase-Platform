using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

/// <summary>
/// Connections to the logs database (applog.AppLog — the Application Audit log), shared by every tenant.
/// ConnectionStrings:LogsConnection when set; otherwise the control connection pointed at the database "PowerBase_Logs"
/// on the same server, so no extra setting is needed where both live together. Migrations: database/migrations/logs,
/// run by the Migrator's "migrate logs".
/// </summary>
public sealed class LogsConnectionFactory
{
    public const string DefaultDatabaseName = "PowerBase_Logs";

    public LogsConnectionFactory(IConfiguration configuration) => ConnectionString = Resolve(configuration);

    public string ConnectionString { get; }

    public SqlConnection Create() => new(ConnectionString);

    public static string Resolve(IConfiguration configuration)
    {
        var explicitConnection = configuration.GetConnectionString("LogsConnection");
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;

        var control = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Neither ConnectionStrings:LogsConnection nor DefaultConnection is configured.");
        return new SqlConnectionStringBuilder(control) { InitialCatalog = DefaultDatabaseName }.ConnectionString;
    }
}
