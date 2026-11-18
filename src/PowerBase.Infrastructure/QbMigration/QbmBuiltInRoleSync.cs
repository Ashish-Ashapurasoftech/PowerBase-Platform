using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Gives PowerBase's built-in roles of a migrated app (Administrator, Participant, Viewer) QuickBase's app-level switches
/// ("Manage users" / Sharing, "Edit app" / Partial or Full Admin), read from the app's QBL export. The QBL import leaves built-in
/// roles' permission sets alone by design; this does it for migrated apps only, without changing the import. It uses the import's
/// own rule (<see cref="PblPermissionsApplier.PermissionCodesFor"/>) for what each switch grants, and only adds or removes the
/// codes those two switches control: everything else the built-in role has (record access, PowerFlows, audit) is kept.</summary>
public sealed class QbmBuiltInRoleSync(ITenantConnectionFactory tenant)
{
    public sealed record Change(string Role, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Warnings);

    // Never added or removed by the sync: what the import grants every role, plus pages:read, which PowerBase gives its built-in
    // Viewer and Participant on app creation (CreateAppCommandHandler) and viewers need to open pages, but the import counts as
    // "Edit app". Everything else in the "Edit app" bundle is still reconciled against QuickBase.
    private static readonly HashSet<string> Base = [.. PblPermissionsApplier.PermissionCodesFor(new PblRole()), PermissionCodes.PagesRead];
    public static readonly IReadOnlyList<string> ManageUsersCodes =
        PblPermissionsApplier.PermissionCodesFor(new PblRole { ManageUsers = true }).Where(c => !Base.Contains(c)).ToList();
    public static readonly IReadOnlyList<string> EditAppCodes =
        PblPermissionsApplier.PermissionCodesFor(new PblRole { EditApp = true }).Where(c => !Base.Contains(c)).ToList();

    /// <summary>The roles of the app in a QBL export (the one named <paramref name="appName"/>, or the only one).</summary>
    public static IReadOnlyList<PblRole> RolesFromQbl(string yaml, string appName)
    {
        var results = QblToPblConverter.ConvertAll(QblSerializer.Deserialize(yaml));
        var match = results.Where(r => string.Equals(r.Document.App.Name, appName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0 && results.Count == 1) match = [.. results];
        if (match.Count != 1)
            throw new InvalidOperationException($"The QBL file has {results.Count} apps and none (or several) named '{appName}': " +
                                                string.Join(", ", results.Select(r => r.Document.App.Name)));
        return match[0].Document.Roles;
    }

    /// <summary>The permission codes a built-in role should have: its current ones, with the "Manage users" and "Edit app" codes
    /// set exactly as QuickBase's role has those switches.</summary>
    public static IReadOnlyList<string> Target(IEnumerable<string> current, PblRole qb)
    {
        var codes = current.Except(ManageUsersCodes).Except(EditAppCodes).ToList();
        if (qb.ManageUsers) codes.AddRange(ManageUsersCodes);
        if (qb.EditApp) codes.AddRange(EditAppCodes);
        return codes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<Change>> SyncAsync(long appId, IReadOnlyList<PblRole> qblRoles, bool dryRun, CancellationToken ct = default)
    {
        await using var conn = await tenant.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var builtIn = (await conn.QueryAsync<(long Id, string Name)>(new CommandDefinition(
            "SELECT Id, Name FROM meta.AppRole WHERE AppId = @appId AND IsSystem = 1 AND IsDeleted = 0", new { appId }, cancellationToken: ct))).ToList();
        var changes = new List<Change>();
        foreach (var (roleId, name) in builtIn)
        {
            var qb = qblRoles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (qb is null) continue;
            var warnings = new List<string>();
            if (qb.DisableAccess)
            {
                changes.Add(new Change(name, [], [], ["QuickBase's role has 'Disable access'; the built-in role was left as it is. Review it by hand."]));
                continue;
            }
            if (qb.TablePermissions.Any(p => p.RecordFilter is not null || p.ModifyRecordFilter is not null || p.FieldPermissions.Count > 0))
                warnings.Add("QuickBase's role has custom record rules or field access on some tables; PowerBase's built-in role doesn't take them. Review it by hand.");

            var current = (await conn.QueryAsync<string>(new CommandDefinition("""
                SELECT p.Code FROM meta.AppRolePermission rp JOIN meta.Permission p ON p.Id = rp.PermissionId WHERE rp.AppRoleId = @roleId
                """, new { roleId }, cancellationToken: ct))).ToList();
            var target = Target(current, qb);
            var added = target.Except(current).ToList();
            var removed = current.Except(target).ToList();
            changes.Add(new Change(name, added, removed, warnings));
            if (dryRun || (added.Count == 0 && removed.Count == 0)) continue;

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM meta.AppRolePermission WHERE AppRoleId = @roleId", new { roleId }, tx, cancellationToken: ct));
            if (target.Count > 0)
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId) SELECT @roleId, Id FROM meta.Permission WHERE Code IN @codes
                    """, new { roleId, codes = target }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        return changes;
    }
}
