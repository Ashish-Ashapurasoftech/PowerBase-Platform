using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants.Commands.CreateTenant;
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
    private readonly ISecretStore _secretStore;

    public TenantProvisioningService(
        IControlConnectionFactory controlFactory,
        ITenantConnectionResolver resolver,
        ITenantRepository tenantRepo,
        IConfiguration configuration,
        ISecretStore secretStore)
    {
        _controlFactory = controlFactory;
        _resolver = resolver;
        _tenantRepo = tenantRepo;
        _configuration = configuration;
        _secretStore = secretStore;
    }

    public async Task ProvisionAsync(long tenantId, TenantServerConfig? serverConfig = null, CancellationToken ct = default)
    {
        var databaseName = $"Powerbase_{tenantId}";

        try
        {
            if (serverConfig is not null)
                await ProvisionOnTenantServerAsync(tenantId, databaseName, serverConfig, ct);
            else
                await ProvisionOnControlServerAsync(tenantId, databaseName, ct);

            _resolver.Invalidate(tenantId);
        }
        catch
        {
            try { await _tenantRepo.UpdateProvisioningAsync(tenantId, "Failed", databaseName, 0, ct: ct); }
            catch { /* don't obscure original exception */ }
            throw;
        }
    }

    private async Task ProvisionOnControlServerAsync(long tenantId, string databaseName, CancellationToken ct)
    {
        await CreateDatabaseAsync(_controlFactory.ConnectionString, databaseName, ct);
        var tenantCs = BuildConnectionString(_controlFactory.ConnectionString, databaseName);
        var migrationsPath = FindTenantMigrationsPath();
        await MigrationRunner.RunAsync(tenantCs, migrationsPath, $"Tenant {tenantId}", ct);

        await _tenantRepo.UpdateProvisioningAsync(
            tenantId, "Ready", databaseName, CurrentSchemaVersion, ct: ct);
    }

    private async Task ProvisionOnTenantServerAsync(long tenantId, string databaseName, TenantServerConfig cfg, CancellationToken ct)
    {
        var adminCs = BuildServerConnectionString(cfg);

        // Step 1: create the database using the supplied admin credentials.
        await CreateDatabaseAsync(adminCs, databaseName, ct);

        // Step 2: run baseline migrations as admin.
        var adminTenantCs = BuildConnectionString(adminCs, databaseName);
        var migrationsPath = FindTenantMigrationsPath();
        await MigrationRunner.RunAsync(adminTenantCs, migrationsPath, $"Tenant {tenantId}", ct);

        // Step 3: create a dedicated, restricted login for PowerBase's ongoing use.
        // The admin credentials are used only here and are never persisted.
        var appLoginName = $"pb_t{tenantId}";
        var appPassword = GenerateSecurePassword();
        await CreateAppLoginAsync(adminCs, databaseName, appLoginName, appPassword, ct);

        // Step 4: store the restricted app connection string in Key Vault.
        var appCs = BuildAppLoginConnectionString(cfg, appLoginName, appPassword, databaseName);
        var secretName = $"tenant-{tenantId}-conn";
        var secretRef = await _secretStore.StoreAsync(secretName, appCs, ct);

        await _tenantRepo.UpdateProvisioningAsync(
            tenantId, "Ready", databaseName, CurrentSchemaVersion,
            serverRef: cfg.Host,
            connectionSecretRef: secretRef,
            ct: ct);
    }

    private static async Task CreateAppLoginAsync(
        string adminConnectionString, string databaseName, string loginName, string password, CancellationToken ct)
    {
        // Create the server-level login against master, then the DB user and role membership
        // against the tenant database — all using the admin connection.
        var masterCs = new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var masterConn = new SqlConnection(masterCs);
        await masterConn.OpenAsync(ct);

        // Idempotent: only create if the login does not already exist.
        // CHECK_POLICY is omitted — it is not supported by Azure SQL Database, and the
        // generated password already carries 256 bits of entropy.
        var loginExists = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{loginName}')
                CREATE LOGIN [{loginName}] WITH PASSWORD = '{password}';
            """;
        await using var loginCmd = new SqlCommand(loginExists, masterConn);
        await loginCmd.ExecuteNonQueryAsync(ct);

        var tenantCs = new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        await using var tenantConn = new SqlConnection(tenantCs);
        await tenantConn.OpenAsync(ct);

        // Create the DB user and grant the minimum PowerBase needs:
        //   db_datareader / db_datawriter — read & write rows
        //   db_ddladmin               — CREATE/ALTER tables & columns (the schema engine)
        // Still far below admin: no login management, no security changes, cannot drop the database.
        var userSql = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{loginName}')
                CREATE USER [{loginName}] FOR LOGIN [{loginName}];
            ALTER ROLE db_datareader ADD MEMBER [{loginName}];
            ALTER ROLE db_datawriter ADD MEMBER [{loginName}];
            ALTER ROLE db_ddladmin   ADD MEMBER [{loginName}];
            """;
        await using var userCmd = new SqlCommand(userSql, tenantConn);
        await userCmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildAppLoginConnectionString(
        TenantServerConfig cfg, string loginName, string password, string databaseName)
        => new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{cfg.Host},{cfg.Port}",
            UserID = loginName,
            Password = password,
            InitialCatalog = databaseName,
            Encrypt = cfg.Encrypt,
            TrustServerCertificate = false,
            MultipleActiveResultSets = true,
            ConnectTimeout = 60,
        }.ConnectionString;

    private static string GenerateSecurePassword()
    {
        // 32 random bytes → 44-char base64. Append fixed symbols to satisfy SQL Server
        // complexity requirements (uppercase, lowercase, digit, special already covered by base64 + suffix).
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes) + "Pb1!";
    }

    private static async Task CreateDatabaseAsync(string serverConnectionString, string databaseName, CancellationToken ct)
    {
        var masterCs = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var connection = new SqlConnection(masterCs);
        await connection.OpenAsync(ct);

        var checkSql = "SELECT DB_ID(@dbName)";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@dbName", databaseName);
        var exists = await checkCmd.ExecuteScalarAsync(ct) is not DBNull;

        if (!exists)
        {
            var createSql = $"CREATE DATABASE [{databaseName}]";
            await using var createCmd = new SqlCommand(createSql, connection);
            createCmd.CommandTimeout = 120;
            await createCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string BuildConnectionString(string baseConnectionString, string databaseName)
        => new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

    private static string BuildServerConnectionString(TenantServerConfig cfg)
        => new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{cfg.Host},{cfg.Port}",
            UserID = cfg.AdminLogin,
            Password = cfg.AdminPassword,
            Encrypt = cfg.Encrypt,
            TrustServerCertificate = false,
            MultipleActiveResultSets = true,
            // Azure SQL can be slow to accept connections on a cold/just-created database.
            ConnectTimeout = 60,
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
