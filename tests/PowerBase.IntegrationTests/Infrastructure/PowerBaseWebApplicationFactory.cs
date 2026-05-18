using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Testcontainers.MsSql;

namespace PowerBase.IntegrationTests.Infrastructure;

public class PowerBaseWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();
        await RunMigrationsAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _container.GetConnectionString(),
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
        var files = Directory.GetFiles(migrationsPath, "*.sql")
            .OrderBy(Path.GetFileName)
            .ToList();

        await using var connection = new SqlConnection(_container.GetConnectionString());
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
                if (!string.IsNullOrWhiteSpace(batch))
                    await connection.ExecuteAsync(batch);
            }
        }
    }
}
