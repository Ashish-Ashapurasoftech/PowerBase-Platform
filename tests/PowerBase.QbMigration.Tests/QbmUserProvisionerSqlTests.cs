using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Tests;

/// <summary>A QuickBase Super User is a tenant Administrator in PowerBase (confirmed policy), also when the person already has a real
/// PowerBase account. Only that account's membership of the migration tenant changes: never its password, its other tenants or the
/// platform Super Admin flag. Runs in tempdb with throw-away rows.</summary>
public sealed class QbmUserProvisionerSqlTests : IAsyncLifetime
{
    private const long AdministratorRole = 4_001, UserRole = 4_002, AppRoleId = 4_003, AddedBy = 1;
    private readonly long _tenant = 950_000 + Random.Shared.Next(40_000);
    private readonly long _otherTenant;
    private readonly long _app;
    private readonly long _userId = 9_500_000 + Random.Shared.Next(400_000);
    private readonly string _email;
    private readonly QbmUserProvisioner _sut = new(new SqlDb.Factory(), () => new SqlConnection(SqlDb.ConnectionString));
    private readonly Dictionary<string, long> _roles = new() { ["Administrator"] = AppRoleId };

    public QbmUserProvisionerSqlTests()
    {
        _otherTenant = _tenant + 50_000;
        _app = _tenant + 100_000;
        _email = $"chaim{_userId}@example.test";
    }

    public async Task InitializeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.OpenAsync();
        // Other tests create smaller versions of these tables, so every column is added when missing (nullable or with a default).
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('core') IS NULL EXEC('CREATE SCHEMA core');
            IF OBJECT_ID('core.[User]') IS NULL CREATE TABLE core.[User] (Id BIGINT PRIMARY KEY, EmailNormalized NVARCHAR(320) NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('meta.TenantUser') IS NULL CREATE TABLE meta.TenantUser (TenantId BIGINT NOT NULL, UserId BIGINT NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('meta.AppUser') IS NULL CREATE TABLE meta.AppUser (Id BIGINT IDENTITY(1,1) PRIMARY KEY, AppId BIGINT NOT NULL, UserId BIGINT NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            IF COL_LENGTH('core.[User]', 'Email') IS NULL ALTER TABLE core.[User] ADD Email NVARCHAR(320) NULL;
            IF COL_LENGTH('core.[User]', 'Name') IS NULL ALTER TABLE core.[User] ADD Name NVARCHAR(200) NULL;
            IF COL_LENGTH('core.[User]', 'FirstName') IS NULL ALTER TABLE core.[User] ADD FirstName NVARCHAR(100) NULL;
            IF COL_LENGTH('core.[User]', 'LastName') IS NULL ALTER TABLE core.[User] ADD LastName NVARCHAR(100) NULL;
            IF COL_LENGTH('core.[User]', 'HashedPassword') IS NULL ALTER TABLE core.[User] ADD HashedPassword NVARCHAR(MAX) NULL;
            IF COL_LENGTH('core.[User]', 'LastLoginOn') IS NULL ALTER TABLE core.[User] ADD LastLoginOn DATETIME2 NULL;
            IF COL_LENGTH('core.[User]', 'IsActive') IS NULL ALTER TABLE core.[User] ADD IsActive BIT NULL;
            IF COL_LENGTH('core.[User]', 'IsEmailVerified') IS NULL ALTER TABLE core.[User] ADD IsEmailVerified BIT NULL;
            IF COL_LENGTH('core.[User]', 'CreatedBy') IS NULL ALTER TABLE core.[User] ADD CreatedBy BIGINT NULL;
            IF COL_LENGTH('core.[User]', 'ModifiedOn') IS NULL ALTER TABLE core.[User] ADD ModifiedOn DATETIME2 NULL;
            IF COL_LENGTH('core.[User]', 'ModifiedBy') IS NULL ALTER TABLE core.[User] ADD ModifiedBy BIGINT NULL;
            IF COL_LENGTH('meta.TenantUser', 'TenantRoleId') IS NULL ALTER TABLE meta.TenantUser ADD TenantRoleId BIGINT NULL;
            IF COL_LENGTH('meta.TenantUser', 'IsOwner') IS NULL ALTER TABLE meta.TenantUser ADD IsOwner BIT NULL;
            IF COL_LENGTH('meta.TenantUser', 'IsActive') IS NULL ALTER TABLE meta.TenantUser ADD IsActive BIT NULL;
            IF COL_LENGTH('meta.TenantUser', 'InvitedBy') IS NULL ALTER TABLE meta.TenantUser ADD InvitedBy BIGINT NULL;
            IF COL_LENGTH('meta.TenantUser', 'CreatedBy') IS NULL ALTER TABLE meta.TenantUser ADD CreatedBy BIGINT NULL;
            IF COL_LENGTH('meta.AppUser', 'UserEmail') IS NULL ALTER TABLE meta.AppUser ADD UserEmail NVARCHAR(320) NULL;
            IF COL_LENGTH('meta.AppUser', 'UserName') IS NULL ALTER TABLE meta.AppUser ADD UserName NVARCHAR(200) NULL;
            IF COL_LENGTH('meta.AppUser', 'AppRoleId') IS NULL ALTER TABLE meta.AppUser ADD AppRoleId BIGINT NULL;
            IF COL_LENGTH('meta.AppUser', 'Status') IS NULL ALTER TABLE meta.AppUser ADD Status NVARCHAR(20) NULL;
            IF COL_LENGTH('meta.AppUser', 'AddedBy') IS NULL ALTER TABLE meta.AppUser ADD AddedBy BIGINT NULL;
            IF COL_LENGTH('meta.AppUser', 'UpdatedOn') IS NULL ALTER TABLE meta.AppUser ADD UpdatedOn DATETIME2 NULL;
            """);
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync("""
            DELETE FROM meta.AppUser WHERE AppId = @app;
            DELETE FROM meta.TenantUser WHERE UserId = @userId;
            DELETE FROM core.[User] WHERE Id = @userId;
            """, new { app = _app, userId = _userId });
    }

    /// <summary>A real PowerBase account (has a password and has signed in) that is a "User" in the migration tenant and in another one.</summary>
    private async Task SeedRealAccountAsync(bool tenantMembershipActive)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync("""
            INSERT INTO core.[User] (Id, EmailNormalized, Email, Name, HashedPassword, LastLoginOn, IsActive, IsEmailVerified, IsDeleted)
            VALUES (@userId, @normalized, @email, 'Chaim W', 'real-password-hash', SYSUTCDATETIME(), 1, 1, 0);
            INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsOwner, IsActive, IsDeleted) VALUES (@tenant, @userId, @userRole, 0, @active, 0);
            INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsOwner, IsActive, IsDeleted) VALUES (@otherTenant, @userId, @userRole, 0, 1, 0);
            """, new { userId = _userId, normalized = _email.ToUpperInvariant(), email = _email, tenant = _tenant, otherTenant = _otherTenant, userRole = UserRole, active = tenantMembershipActive });
    }

    private QbUserPlan Plan(bool superUser, bool registered) =>
        new(_email, "Chaim W", "Chaim", "W", "Administrator", registered, superUser, superUser ? "(Super User)" : "Administrator", null);

    private async Task<QbmUserProvisioner.Outcome> ApplyAsync(QbUserPlan plan, bool dryRun = false, bool activate = false) =>
        (await _sut.ApplyAsync(_tenant, _app, [plan], _roles, (AdministratorRole, UserRole), AddedBy, dryRun, default, activate)).Single();

    private async Task<(long? Role, bool Active)> MembershipAsync(long tenant)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var row = await c.QuerySingleAsync<(long? Role, bool Active)>("SELECT TenantRoleId, IsActive FROM meta.TenantUser WHERE TenantId = @tenant AND UserId = @userId",
            new { tenant, userId = _userId });
        return row;
    }

    private async Task<string?> PasswordAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        return await c.ExecuteScalarAsync<string?>("SELECT HashedPassword FROM core.[User] WHERE Id = @userId", new { userId = _userId });
    }

    [SqlFact]
    public async Task A_super_user_with_a_real_account_becomes_tenant_administrator_and_nothing_else_changes()
    {
        await SeedRealAccountAsync(tenantMembershipActive: true);

        var outcome = await ApplyAsync(Plan(superUser: true, registered: true));

        (await MembershipAsync(_tenant)).Should().Be((AdministratorRole, true));
        outcome.Tenant.Should().Contain("Administrator");
        outcome.Tenant.Should().StartWith("corrected", "the user summary counts a tenant result that starts with 'corrected' as a change");
        (outcome.Warning ?? "").Should().NotContain("left as it is");
        (await MembershipAsync(_otherTenant)).Should().Be((UserRole, true), "another tenant of the same account is never touched");
        (await PasswordAsync()).Should().Be("real-password-hash", "a real account's own data is never changed");
    }

    [SqlFact]
    public async Task A_super_users_inactive_membership_is_activated_when_quickbase_says_registered_without_any_extra_flag()
    {
        await SeedRealAccountAsync(tenantMembershipActive: false);

        var outcome = await ApplyAsync(Plan(superUser: true, registered: true), activate: false);

        (await MembershipAsync(_tenant)).Should().Be((AdministratorRole, true));
        outcome.Tenant.Should().Contain("activated");
        (await MembershipAsync(_otherTenant)).Should().Be((UserRole, true));
    }

    [SqlFact]
    public async Task A_super_user_who_is_not_registered_is_promoted_but_never_activated()
    {
        await SeedRealAccountAsync(tenantMembershipActive: false);

        await ApplyAsync(Plan(superUser: true, registered: false));

        (await MembershipAsync(_tenant)).Should().Be((AdministratorRole, false), "the role follows the Super User policy, the status is only ever raised for registered people");
    }

    [SqlFact]
    public async Task A_dry_run_reports_the_promotion_but_writes_nothing()
    {
        await SeedRealAccountAsync(tenantMembershipActive: false);

        var outcome = await ApplyAsync(Plan(superUser: true, registered: true), dryRun: true);

        outcome.Tenant.Should().Contain("Administrator");
        (await MembershipAsync(_tenant)).Should().Be((UserRole, false), "a dry run must leave the database as it was");
    }

    [SqlFact]
    public async Task A_super_user_who_is_already_administrator_is_left_alone()
    {
        await SeedRealAccountAsync(tenantMembershipActive: true);
        await using (var c = new SqlConnection(SqlDb.ConnectionString))
            await c.ExecuteAsync("UPDATE meta.TenantUser SET TenantRoleId = @admin WHERE TenantId = @tenant AND UserId = @userId", new { admin = AdministratorRole, tenant = _tenant, userId = _userId });

        var outcome = await ApplyAsync(Plan(superUser: true, registered: true));

        outcome.Tenant.Should().Contain("not changed");
        (await MembershipAsync(_tenant)).Should().Be((AdministratorRole, true));
    }

    [SqlFact]
    public async Task An_ordinary_user_with_a_real_account_keeps_the_tenant_role_they_have()
    {
        await SeedRealAccountAsync(tenantMembershipActive: true);

        await ApplyAsync(Plan(superUser: false, registered: true));

        (await MembershipAsync(_tenant)).Should().Be((UserRole, true), "only QuickBase Super Users are promoted");
    }
}
