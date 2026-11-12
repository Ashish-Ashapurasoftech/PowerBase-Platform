using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Creates PowerBase users for QuickBase users the data refers to, as decided for the migration: inactive,
/// no invitation email, added to the tenant (inactive) and to the app with one chosen role. The normal invite flow
/// always emails, so it is not used. A user who already exists is only linked, never changed.</summary>
public sealed class QbmUserProvisioner(ITenantConnectionFactory tenant, Func<SqlConnection> control)
{
    public sealed record Outcome(string Email, string Action, long UserId);

    public async Task<IReadOnlyList<Outcome>> ProvisionAsync(long tenantId, long appId, long appRoleId, long tenantRoleId, long addedBy,
        IReadOnlyCollection<string> emails, bool dryRun, CancellationToken ct = default)
    {
        var outcomes = new List<Outcome>();
        foreach (var email in emails.Select(e => e.Trim()).Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = email.ToUpperInvariant();
            await using var conn = control();
            await conn.OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT Id FROM core.[User] WHERE EmailNormalized = @normalized AND IsDeleted = 0", new { normalized }, cancellationToken: ct));
            if (dryRun)
            {
                outcomes.Add(new(email, existing is null ? "would create" : "would link", existing ?? 0));
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            var userId = existing ?? await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO core.[User] (Email, EmailNormalized, HashedPassword, Name, IsEmailVerified, IsActive, IsDeleted, CreatedBy)
                OUTPUT INSERTED.Id VALUES (@email, @normalized, '', @name, 0, 0, 0, @addedBy)
                """, new { email, normalized, name = email.Split('@')[0], addedBy }, tx, cancellationToken: ct));
            // Tenant link only if there is none (an existing membership, active or not, is left exactly as it is).
            await conn.ExecuteAsync(new CommandDefinition("""
                IF NOT EXISTS (SELECT 1 FROM meta.TenantUser WHERE TenantId = @tenantId AND UserId = @userId)
                    INSERT INTO meta.TenantUser (TenantId, UserId, TenantRoleId, IsOwner, IsActive, InvitedBy, CreatedBy)
                    VALUES (@tenantId, @userId, @tenantRoleId, 0, 0, @addedBy, @addedBy)
                """, new { tenantId, userId, tenantRoleId, addedBy }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);

            await using var t = await tenant.CreateAsync(ct);
            await t.ExecuteAsync(new CommandDefinition("""
                IF NOT EXISTS (SELECT 1 FROM meta.AppUser WHERE AppId = @appId AND UserId = @userId AND IsDeleted = 0)
                    INSERT INTO meta.AppUser (AppId, UserId, UserEmail, UserName, AppRoleId, AddedBy)
                    VALUES (@appId, @userId, @email, @name, @appRoleId, @addedBy)
                """, new { appId, userId, email, name = email.Split('@')[0], appRoleId, addedBy }, cancellationToken: ct));
            outcomes.Add(new(email, existing is null ? "created" : "linked", userId));
        }
        return outcomes;
    }
}
