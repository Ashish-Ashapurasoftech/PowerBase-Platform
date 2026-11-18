namespace PowerBase.Application.QbMigration;

/// <summary>A role of the QuickBase app. <paramref name="AccessId"/>: 1 Administrator, 2 Basic Access with Share, 3 Basic Access, 0 None.</summary>
public sealed record QbRole(int Id, string Name, int AccessId);

/// <summary>A user of the QuickBase app with the roles QuickBase lists for them (directly or through a group).</summary>
/// <param name="IsRegistered">QuickBase's "Registered" status (API_GetUserInfo isVerified = 1); null when QuickBase didn't say.</param>
public sealed record QbAppUser(string QbUserId, string? Email, string Name, string? FirstName, string? LastName, bool? IsRegistered,
    IReadOnlyList<QbRole> Roles);

/// <summary>Everything the user migration reads from QuickBase about an app: its roles in QuickBase's precedence order (the
/// order of the app's Roles page; a user in several roles gets the first) and its users.</summary>
public sealed record QbUserDirectory(IReadOnlyList<QbRole> RoleOrder, IReadOnlyList<QbAppUser> Users, IReadOnlyList<string> Warnings);

/// <summary>What the migration gives one user in PowerBase.</summary>
/// <param name="AppRoleName">The PowerBase app role (same name as the QuickBase role).</param>
/// <param name="IsActive">Registered in QuickBase: account, tenant membership and app membership active, email verified.</param>
/// <param name="IsSuperUser">QuickBase Super User (account/realm admin): tenant Administrator and app Administrator in PowerBase.</param>
/// <param name="QbRoles">Every role QuickBase lists for the user, for the report.</param>
public sealed record QbUserPlan(string Email, string Name, string? FirstName, string? LastName, string AppRoleName, bool IsActive,
    bool IsSuperUser, string QbRoles, string? Note);

public sealed record QbUserPlanResult(IReadOnlyList<QbUserPlan> Users, IReadOnlyList<string> MissingRoles, IReadOnlyList<string> Warnings)
{
    /// <summary>The users can be written only when every role they need exists in PowerBase (or is "None", which the migration creates).</summary>
    public bool CanApply => MissingRoles.Count == 0;
}

/// <summary>Decides, from QuickBase alone, the PowerBase role and status of every app user. Rules agreed for the migration:
/// <list type="bullet">
/// <item>Registered in QuickBase → active everywhere and email verified; anything else → inactive.</item>
/// <item>The exact QuickBase role, matched by name; several roles → the first in QuickBase's role order (QuickBase's precedence).</item>
/// <item>QuickBase's built-in "None" role (no role assigned) → a PowerBase role named "None" without permissions; status still follows Registered.</item>
/// <item>QuickBase Super User (listed with role id 0) → app Administrator and tenant Administrator. Never PowerBase's platform Super Admin.</item>
/// <item>A role PowerBase doesn't have stops the migration: roles and their permissions come from the QBL import, never from defaults.</item>
/// </list></summary>
public static class QbUserPlanner
{
    /// <summary>QuickBase's built-in role ids: 9 = "None" (no access), 0 = access without an app role (Super User).</summary>
    public const int NoneRoleId = 9, SuperUserRoleId = 0;
    public const string NoneRoleName = "None", AdministratorRoleName = "Administrator";

    public static QbUserPlanResult Plan(QbUserDirectory directory, IReadOnlyCollection<string> pbRoleNames)
    {
        var order = directory.RoleOrder.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var pbRoles = new HashSet<string>(pbRoleNames, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>(directory.Warnings);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<QbUserPlan>();

        foreach (var group in directory.Users.GroupBy(u => u.Email?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || !group.Key.Contains('@'))
            {
                foreach (var u in group)
                    warnings.Add($"QuickBase user '{u.Name}' ({u.QbUserId}) has no email address in QuickBase; not migrated.");
                continue;
            }
            // The same person can be listed directly and through groups: all their roles count.
            var first = group.First();
            var roles = group.SelectMany(u => u.Roles).DistinctBy(r => r.Id).ToList();
            var registered = group.Select(u => u.IsRegistered).FirstOrDefault(r => r.HasValue);
            if (registered is null) warnings.Add($"{group.Key}: QuickBase did not say whether the user is registered; migrated as inactive.");

            var superUser = roles.Any(r => r.Id == SuperUserRoleId);
            var real = roles.Where(r => r.Id is not (SuperUserRoleId or NoneRoleId))
                .OrderBy(r => order.TryGetValue(r.Id, out var i) ? i : int.MaxValue).ToList();
            string role;
            string? note = null;
            if (superUser)
            {
                role = AdministratorRoleName;
                note = "QuickBase Super User: tenant Administrator";
            }
            else if (real.Count > 0)
            {
                role = real[0].Name;
                if (real.Count > 1) note = $"in {real.Count} QuickBase roles; '{role}' comes first in QuickBase's role order";
            }
            else role = NoneRoleName;

            if (!role.Equals(NoneRoleName, StringComparison.OrdinalIgnoreCase) && !pbRoles.Contains(role)) missing.Add(role);
            var qbRoles = string.Join(", ", roles.Select(r => r.Id == SuperUserRoleId ? "(Super User)" : r.Name.Length == 0 ? $"#{r.Id}" : r.Name));
            plans.Add(new QbUserPlan(group.Key, first.Name, first.FirstName, first.LastName, role, registered == true, superUser, qbRoles, note));
        }
        return new QbUserPlanResult(plans.OrderBy(p => p.Email, StringComparer.OrdinalIgnoreCase).ToList(), missing.ToList(), warnings);
    }
}
