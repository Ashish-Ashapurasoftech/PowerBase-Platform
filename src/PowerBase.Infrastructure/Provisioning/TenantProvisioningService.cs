using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Migrations;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Provisioning;

public class TenantProvisioningService : ITenantProvisioningService
{
    private const int CurrentSchemaVersion = 1;

    private readonly IControlConnectionFactory _controlFactory;
    private readonly ITenantConnectionResolver _resolver;
    private readonly ITenantRepository _tenantRepo;
    private readonly IConfiguration _configuration;

    public TenantProvisioningService(
        IControlConnectionFactory controlFactory,
        ITenantConnectionResolver resolver,
        ITenantRepository tenantRepo,
        IConfiguration configuration)
    {
        _controlFactory = controlFactory;
        _resolver = resolver;
        _tenantRepo = tenantRepo;
        _configuration = configuration;
    }

    public async Task ProvisionAsync(long tenantId, CancellationToken ct = default)
    {
        var databaseName = $"Powerbase_{tenantId}";

        try
        {
            await CreateDatabaseAsync(databaseName, ct);

            var tenantCs = BuildTenantConnectionString(databaseName);
            var migrationsPath = FindTenantMigrationsPath();
            await MigrationRunner.RunAsync(tenantCs, migrationsPath, $"Tenant {tenantId}", ct);

            await _tenantRepo.UpdateProvisioningAsync(
                tenantId, "Ready", databaseName, CurrentSchemaVersion, ct);

            _resolver.Invalidate(tenantId);
        }
        catch
        {
            // Best-effort: mark failed so operators can see it
            try { await _tenantRepo.UpdateProvisioningAsync(tenantId, "Failed", databaseName, 0, ct); }
            catch { /* don't obscure original exception */ }
            throw;
        }
    }

    private async Task CreateDatabaseAsync(string databaseName, CancellationToken ct)
    {
        // Must run CREATE DATABASE outside a transaction against master or any DB on the same server.
        // We connect to the control DB server (InitialCatalog = master to be safe).
        var masterCs = new SqlConnectionStringBuilder(_controlFactory.ConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var connection = new SqlConnection(masterCs);
        await connection.OpenAsync(ct);

        // Idempotent: only create if not exists
        var checkSql = "SELECT DB_ID(@dbName)";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@dbName", databaseName);
        var exists = await checkCmd.ExecuteScalarAsync(ct) is not DBNull;

        if (!exists)
        {
            // Database name is system-generated (Powerbase_{id}) — no user input involved.
            var createSql = $"CREATE DATABASE [{databaseName}]";
            await using var createCmd = new SqlCommand(createSql, connection);
            createCmd.CommandTimeout = 60;
            await createCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private string BuildTenantConnectionString(string databaseName)
        => new SqlConnectionStringBuilder(_controlFactory.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

    private string FindTenantMigrationsPath()
    {
        var configured = _configuration["Migrations:TenantPath"];
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
            return configured;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "database", "migrations", "tenant");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate 'database/migrations/tenant/'. Set Migrations:TenantPath in configuration.");
    }
}
