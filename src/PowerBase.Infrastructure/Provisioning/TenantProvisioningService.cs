using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<TenantProvisioningService>? _logger;

    public TenantProvisioningService(
        IControlConnectionFactory controlFactory,
        ITenantConnectionResolver resolver,
        ITenantRepository tenantRepo,
        IConfiguration configuration,
        ISecretStore secretStore,
        ILogger<TenantProvisioningService>? logger = null)
    {
        _controlFactory = controlFactory;
        _resolver = resolver;
        _tenantRepo = tenantRepo;
        _configuration = configuration;
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task ProvisionAsync(long tenantId, TenantServerConfig? serverConfig = null, CancellationToken ct = default)
    {
        var databaseName = $"Powerbase_{tenantId}";

        try
        {
            _logger?.LogInformation("Starting database provisioning for tenant {TenantId} (Database: {DatabaseName}).", tenantId, databaseName);

            if (serverConfig is not null)
                await ProvisionOnTenantServerAsync(tenantId, databaseName, serverConfig, ct);
            else
                await ProvisionOnControlServerAsync(tenantId, databaseName, ct);

            _resolver.Invalidate(tenantId);

            _logger?.LogInformation("Database provisioning successfully completed for tenant {TenantId}.", tenantId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Database provisioning failed for tenant {TenantId} (Database: {DatabaseName}).", tenantId, databaseName);
            try { await _tenantRepo.UpdateProvisioningAsync(tenantId, "Failed", databaseName, 0, ct: ct); }
            catch { /* don't obscure original exception */ }
            throw;
        }
    }

    private async Task ProvisionOnControlServerAsync(long tenantId, string databaseName, CancellationToken ct)
    {
        await CreateDatabaseAsync(_controlFactory.ConnectionString, databaseName, ct);
        var tenantCs = BuildConnectionString(_controlFactory.ConnectionString, databaseName);

        // Azure SQL Database cold-start readiness check
        await WaitForDatabaseReadyAsync(tenantCs, ct);

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

        // Step 2: wait for database to be online on Azure SQL.
        var adminTenantCs = BuildConnectionString(adminCs, databaseName);
        await WaitForDatabaseReadyAsync(adminTenantCs, ct);

        // Step 3: run baseline migrations as admin.
        var migrationsPath = FindTenantMigrationsPath();
        await MigrationRunner.RunAsync(adminTenantCs, migrationsPath, $"Tenant {tenantId}", ct);

        // Step 4: create a dedicated, restricted login for PowerBase's ongoing use.
        var appLoginName = $"pb_t{tenantId}";
        var appPassword = GenerateSecurePassword();
        await CreateAppLoginAsync(adminCs, databaseName, appLoginName, appPassword, ct);

        // Step 5: store the restricted app connection string in Key Vault.
        var appCs = BuildAppLoginConnectionString(cfg, appLoginName, appPassword, databaseName);
        var secretName = $"tenant-{tenantId}-conn";
        var secretRef = await _secretStore.StoreAsync(secretName, appCs, ct);

        await _tenantRepo.UpdateProvisioningAsync(
            tenantId, "Ready", databaseName, CurrentSchemaVersion,
            serverRef: cfg.Host,
            connectionSecretRef: secretRef,
            ct: ct);
    }

    private async Task CreateDatabaseAsync(string serverConnectionString, string databaseName, CancellationToken ct)
    {
        var masterCs = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var connection = new SqlConnection(masterCs);
        await connection.OpenAsync(ct);

        var checkSql = "SELECT COUNT(1) FROM sys.databases WHERE name = @dbName";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@dbName", databaseName);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct) ?? 0) > 0;

        if (!exists)
        {
            _logger?.LogInformation("Creating database [{DatabaseName}]...", databaseName);
            try
            {
                var createSql = $"CREATE DATABASE [{databaseName}]";
                await using var createCmd = new SqlCommand(createSql, connection);
                createCmd.CommandTimeout = 180;
                await createCmd.ExecuteNonQueryAsync(ct);
                _logger?.LogInformation("Database [{DatabaseName}] CREATE command executed successfully.", databaseName);
            }
            catch (SqlException ex) when (ex.Number == 1801) // 1801 = Database already exists
            {
                _logger?.LogWarning("Database [{DatabaseName}] already exists (Error 1801). Continuing provisioning.", databaseName);
            }
        }
        else
        {
            _logger?.LogInformation("Database [{DatabaseName}] already exists in sys.databases.", databaseName);
        }
    }

    private async Task WaitForDatabaseReadyAsync(string connectionString, CancellationToken ct)
    {
        const int maxAttempts = 15;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct);
                _logger?.LogInformation("Database connection successfully established on attempt {Attempt}.", attempt);
                return;
            }
            catch (SqlException ex) when (attempt < maxAttempts && IsTransientAzureSqlError(ex))
            {
                _logger?.LogWarning("Attempt {Attempt}/{MaxAttempts} to connect to database failed with SQL error {Number} ({Message}). Retrying in {Delay}s...",
                    attempt, maxAttempts, ex.Number, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            }
        }
    }

    private static bool IsTransientAzureSqlError(SqlException ex)
    {
        return ex.Number switch
        {
            40613 or 40197 or 40501 or 18456 or 233 or -2 or 0 => true,
            _ => false
        };
    }

    private static async Task CreateAppLoginAsync(
        string adminConnectionString, string databaseName, string loginName, string password, CancellationToken ct)
    {
        var masterCs = new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var masterConn = new SqlConnection(masterCs);
        await masterConn.OpenAsync(ct);

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

    private static string BuildConnectionString(string baseConnectionString, string databaseName)
        => new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName,
            ConnectTimeout = 60
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
            ConnectTimeout = 60,
        }.ConnectionString;

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
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes) + "Pb1!";
    }

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
