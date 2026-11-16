using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Testcontainers.MsSql;

namespace PowerBase.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the API against a real SQL Server. By default that is a throwaway Testcontainers instance. Where Docker is not available, set
/// <c>POWERBASE_TEST_SQL_CONNECTION</c> to a connection string for a THROWAWAY SQL Server (for example a dedicated LocalDB instance) and the
/// tests use it instead: a fresh control database is created for the run (named so it does not match the Powerbase_% pattern the ops script fix_security_options.sql loops over) and dropped afterwards. Tenant databases the tests provision are
/// created on the same server, so never point this at a server whose databases matter.
/// </summary>
public class PowerBaseWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string EXTERNAL_SQL_VARIABLE = "POWERBASE_TEST_SQL_CONNECTION";

    private readonly string? _externalServerConnection = Environment.GetEnvironmentVariable(EXTERNAL_SQL_VARIABLE);
    private readonly MsSqlContainer? _container;
    private string _connectionString = string.Empty;
    private string? _externalDatabase;

    public PowerBaseWebApplicationFactory()
    {
        if (string.IsNullOrWhiteSpace(_externalServerConnection))
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        else
        {
            _externalDatabase = $"pbtest_{Guid.NewGuid():N}";
            await using (var server = new SqlConnection(_externalServerConnection))
            {
                await server.OpenAsync();
                await server.ExecuteAsync($"CREATE DATABASE [{_externalDatabase}]");
            }
            _connectionString = new SqlConnectionStringBuilder(_externalServerConnection) { InitialCatalog = _externalDatabase }.ConnectionString;
        }

        await RunMigrationsAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        if (_externalDatabase is not null)
        {
            try
            {
                SqlConnection.ClearAllPools();
                await using var server = new SqlConnection(_externalServerConnection);
                await server.OpenAsync();

                // The tenant databases this run provisioned (and only those: they are listed in this run's own control database).
                var tenantDatabases = (await server.QueryAsync<string>($"SELECT DatabaseName FROM [{_externalDatabase}].meta.Tenant WHERE DatabaseName IS NOT NULL")).ToList();
                foreach (var database in tenantDatabases.Append(_externalDatabase))
                    await server.ExecuteAsync($"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
            }
            catch
            {
                // Best effort: the server is throwaway by contract.
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["Jwt:SecretKey"] = "integration-test-secret-key-minimum-32chars!!",
                ["Jwt:ExpiresInMinutes"] = "60",
                ["Jwt:Issuer"] = "powerbase-test",
                ["Jwt:Audience"] = "powerbase-test",
                ["Serilog:MinimumLevel:Default"] = "Fatal",
            });
        });
    }

    private async Task RunMigrationsAsync()
    {
        var migrationsPath = Path.Combine(AppContext.BaseDirectory, "Migrations");
        // The control database is the base scripts plus the control/ ones (tenant routing, queues, …), in that order.
        var controlPath = Path.Combine(migrationsPath, "control");
        var files = Directory.GetFiles(migrationsPath, "*.sql")
            .OrderBy(Path.GetFileName)
            .Concat(Directory.Exists(controlPath) ? Directory.GetFiles(controlPath, "*.sql").OrderBy(Path.GetFileName) : [])
            .ToList();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file);
            var batches = System.Text.RegularExpressions.Regex.Split(
                sql,
                @"^\s*GO\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                try
                {
                    await connection.ExecuteAsync(batch);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Migration {Path.GetFileName(file)} failed: {ex.Message}", ex);
                }
            }
        }
    }
}
