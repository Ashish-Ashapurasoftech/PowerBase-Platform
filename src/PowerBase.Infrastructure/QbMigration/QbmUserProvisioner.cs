using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Writes the users of a migrated app as <see cref="QbUserPlanner"/> decided, without any invitation email (the invite
/// flow always emails, so it is not used).
/// <list type="bullet">
/// <item>Account: created with QuickBase's status (Registered → active and email verified, otherwise inactive). An account the
/// migration created earlier (no password, never signed in) is brought to QuickBase's status. A real PowerBase account (has a
/// password or has signed in) is never changed.</item>
/// <item>Tenant membership: added if missing with the same status; tenant "User" role, or tenant "Administrator" for a QuickBase
/// Super User. A membership of a real account is only ever activated (QuickBase: Registered, membership inactive); its role
/// is never changed and it is never deactivated.</item>
/// <item>App membership: the exact role and status, added or corrected.</item>
/// </list></summary>
public sealed class QbmUserProvisioner(ITenantConnectionFactory tenant, Func<SqlConnection> control)
{
    /// <param name="Account">created / status updated / unchanged / existing PowerBase account (not changed)</param>
    /// <param name="App">added / role or status corrected / unchanged</param>
    public sealed record Outcome(QbUserPlan Plan, long UserId, string Account, string Tenant, string App, string? Warning);

    public async Task<string> AppNameAsync(long appId, CancellationToken ct = default)
    {
        await using var t = await tenant.CreateAsync(ct);
        return await t.QuerySingleAsync<string>(new CommandDefinition("SELECT Name FROM meta.App WHERE Id = @appId", new { appId }, cancellationToken: ct));
    }

    public async Task<Dictionary<string, long>> AppRolesAsync(long appId, CancellationToken ct = default)
    {
        await using var t = await tenant.CreateAsync(ct);
        var rows = await t.QueryAsync<(long Id, string Name)>(new CommandDefinition(
            "SELECT Id, Name FROM meta.AppRole WHERE AppId = @appId AND IsDeleted = 0", new { appId }, cancellationToken: ct));
        var roles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows) roles.TryAdd(name.Trim(), id);
        return roles;
    }

    /// <summary>The tenant's "Administrator" and "User" tenant roles.</summary>
    public async Task<(long Administrator, long User)> TenantRolesAsync(long tenantId, CancellationToken ct = default)
    {
        await using var c = control();
        var rows = (await c.QueryAsync<(long Id, string Name)>(new CommandDefinition(
            "SELECT Id, Name FROM meta.TenantRole WHERE TenantId = @tenantId", new { tenantId }, cancellationToken: ct))).ToList();
        long Find(string name) => rows.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).FirstOrDefault() is var id and > 0
            ? id : throw new InvalidOperationException($"Tenant {tenantId} has no tenant role named '{name}'.");
        return (Find("Administrator"), Find("User"));
    }

    /// <summary>The app's "None" role (QuickBase's built-in no-access role): created without any permission if missing.</summary>
    public async Task<long?> EnsureNoneRoleAsync(long appId, long addedBy, bool dryRun, CancellationToken ct = default)
    {
        await using var t = await tenant.CreateAsync(ct);
        var id = await t.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT TOP 1 Id FROM meta.AppRole WHERE AppId = @appId AND Name = @name AND IsDeleted = 0",
            new { appId, name = QbUserPlanner.NoneRoleName }, cancellationToken: ct));
        if (id is not null || dryRun) return id;
        return await t.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO meta.AppRole (AppId, Name, IsDefault, IsSystem, CreatedBy, ManageableRolesType, Rank)
            OUTPUT INSERTED.Id VALUES (@appId, @name, 0, 0, @addedBy, 'None', 3)
            """, new { appId, name = QbUserPlanner.NoneRoleName, addedBy }, cancellationToken: ct));
    }

    private sealed record MemberRow(long? TenantRoleId, bool IsActive);
    private sealed record AppUserRow(long Id, long AppRoleId, string Status);

    private sealed record Account(long Id, string? HashedPassword, DateTime? LastLoginOn, bool IsActive, bool IsEmailVerified)
    {
        /// <summary>Created by the migration and never used: no password set, never signed in.</summary>
        public bool OwnedByMigration => string.IsNullOrEmpty(HashedPassword) && LastLoginOn is null;
    }

    public async Task<IReadOnlyList<Outcome>> ApplyAsync(long tenantId, long appId, IReadOnlyList<QbUserPlan> plans, IReadOnlyDictionary<string, long> roleIds,
        (long Administrator, long User) tenantRoles, long addedBy, bool dryRun, CancellationToken ct = default)
    {
        var outcomes = new List<Outcome>();
        foreach (var p in plans)
        {
            var normalized = p.Email.ToUpperInvariant();
            await using var c = control();
            await c.OpenAsync(ct);
            await using var tx = dryRun ? null : await c.BeginTransactionAsync(ct);
            var existing = await c.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
                "SELECT Id, HashedPassword, LastLoginOn, IsActive, IsEmailVerified FROM core.[User] WHERE EmailNormalized = @normalized AND IsDeleted = 0",
                new { normalized }, tx, cancellationToken: ct));
            string? warning = null;

            // Account
            string account;
            long userId = existing?.Id ?? 0;
            if (existing is null)
            {
                account = p.IsActive ? "created (active)" : "created (inactive)";
                if (!dryRun)
                    userId = await c.ExecuteScalarAsync<long>(new CommandDefinition("""
                        INSERT INTO core.[User] (Email, EmailNormalized, HashedPassword, Name, FirstName, LastName, IsEmailVerified, IsActive, IsDeleted, CreatedBy)
                        OUTPUT INSERTED.Id VALUES (@email, @normalized, '', @name, @first, @last, @active, @active, 0, @addedBy)
                        """, new { email = p.Email, normalized, name = p.Name, first = p.FirstName, last = p.LastName, active = p.IsActive, addedBy }, tx, cancellationToken: ct));
            }
            else if (existing.OwnedByMigration)
            {
                var changed = existing.IsActive != p.IsActive || existing.IsEmailVerified != p.IsActive;
                account = changed ? (p.IsActive ? "status corrected → active" : "status corrected → inactive") : "unchanged";
                if (changed && !dryRun)
                    await c.ExecuteAsync(new CommandDefinition(
                        "UPDATE core.[User] SET IsActive = @active, IsEmailVerified = @active, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @addedBy WHERE Id = @id",
                        new { active = p.IsActive, addedBy, id = existing.Id }, tx, cancellationToken: ct));
            }
            else
            {
                account = "existing PowerBase account (not changed)";
                if (existing.IsActive != p.IsActive)
                    warning = $"PowerBase account is {(existing.IsActive ? "active" : "inactive")}, QuickBase says {(p.IsActive ? "registered" : "not registered")}; the account was left as it is.";
            }

            // Tenant membership
            var member = userId == 0 ? null : await c.QuerySingleOrDefaultAsync<MemberRow>(new CommandDefinition(
                "SELECT TenantRoleId, IsActive FROM meta.TenantUser WHERE TenantId = @tenantId AND UserId = @userId", new { tenantId, userId }, tx, cancellationToken: ct));
            string tenantAction;
            var wantRole = p.IsSuperUser ? tenantRoles.Administrator : member?.TenantRoleId ?? tenantRoles.User;
            if (member is null)
            {
                tenantAction = $"added ({(p.IsSuperUser ? "Administrator" : "User")}, {(p.IsActive ? "active" : "inactive")})";
                if (!dryRun)
                    await c.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsOwner, IsActive, InvitedBy, CreatedBy)
                        VALUES (@tenantId, @userId, @wantRole, 0, @active, @addedBy, @addedBy)
                        """, new { tenantId, userId, wantRole, active = p.IsActive, addedBy }, tx, cancellationToken: ct));
            }
            else if (existing?.OwnedByMigration != false)
            {
                var changed = member.IsActive != p.IsActive || member.TenantRoleId != wantRole;
                tenantAction = changed ? $"corrected ({(wantRole == tenantRoles.Administrator ? "Administrator" : "User")}, {(p.IsActive ? "active" : "inactive")})" : "unchanged";
                if (changed && !dryRun)
                    await c.ExecuteAsync(new CommandDefinition(
                        "UPDATE meta.TenantUser SET IsActive = @active, TenantRoleId = @wantRole WHERE TenantId = @tenantId AND UserId = @userId",
                        new { active = p.IsActive, wantRole, tenantId, userId }, tx, cancellationToken: ct));
            }
            else if (p.IsActive && !member.IsActive)
            {
                // A real account: only an inactive membership of this tenant is activated when QuickBase says Registered. Its role,
                // the account itself and its other tenants are never touched, and an active membership is never deactivated.
                tenantAction = "activated (QuickBase: Registered; role not changed)";
                if (!dryRun)
                    await c.ExecuteAsync(new CommandDefinition(
                        "UPDATE meta.TenantUser SET IsActive = 1 WHERE TenantId = @tenantId AND UserId = @userId AND IsActive = 0",
                        new { tenantId, userId }, tx, cancellationToken: ct));
                if (p.IsSuperUser && member.TenantRoleId != tenantRoles.Administrator)
                    warning = (warning is null ? "" : warning + " ") + "QuickBase Super User, but not a tenant Administrator in PowerBase; left as it is.";
            }
            else
            {
                tenantAction = "existing membership (not changed)";
                if (p.IsSuperUser && member.TenantRoleId != tenantRoles.Administrator)
                    warning = (warning is null ? "" : warning + " ") + "QuickBase Super User, but not a tenant Administrator in PowerBase; left as it is.";
            }
            if (tx is not null) await tx.CommitAsync(ct);

            // App membership
            var roleId = roleIds[p.AppRoleName];
            var status = p.IsActive ? "Active" : "InActive";
            await using var t = await tenant.CreateAsync(ct);
            var appUser = userId == 0 ? null : await t.QuerySingleOrDefaultAsync<AppUserRow>(new CommandDefinition(
                "SELECT TOP 1 Id, AppRoleId, Status FROM meta.AppUser WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0 ORDER BY Id",
                new { appId, userId }, cancellationToken: ct));
            string app;
            if (appUser is null)
            {
                app = $"added ({p.AppRoleName}, {status})";
                if (!dryRun)
                    await t.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO meta.AppUser (AppId, UserId, UserEmail, UserName, AppRoleId, Status, AddedBy)
                        VALUES (@appId, @userId, @email, @name, @roleId, @status, @addedBy)
                        """, new { appId, userId, email = p.Email, name = p.Name, roleId, status, addedBy }, cancellationToken: ct));
            }
            else if (appUser.AppRoleId != roleId || !string.Equals(appUser.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                var oldRole = roleIds.FirstOrDefault(r => r.Value == appUser.AppRoleId).Key ?? $"#{appUser.AppRoleId}";
                app = $"corrected ({oldRole}, {appUser.Status} → {p.AppRoleName}, {status})";
                if (!dryRun)
                    await t.ExecuteAsync(new CommandDefinition(
                        "UPDATE meta.AppUser SET AppRoleId = @roleId, Status = @status, UpdatedOn = SYSUTCDATETIME() WHERE Id = @id",
                        new { roleId, status, id = appUser.Id }, cancellationToken: ct));
            }
            else app = "unchanged";

            outcomes.Add(new Outcome(p, userId, account, tenantAction, app, warning));
        }
        return outcomes;
    }
}
