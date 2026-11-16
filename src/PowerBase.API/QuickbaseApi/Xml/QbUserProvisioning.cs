using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// API_ProvisionUser: gives a person access to an app WITHOUT sending an invitation (Quickbase sends it later through
/// API_SendInvitation). Mirrors the "new or pending user" path of the existing invite command — pending user, inactive
/// tenant membership, pending app-role grant — minus the e-mail, and applies the same role-hierarchy rules.
/// The existing invite command itself is not changed.
/// </summary>
public sealed class QbUserProvisioning
{
    /// <summary>Same lifetime the invite command gives its tokens.</summary>
    public static readonly TimeSpan GRANT_LIFETIME = TimeSpan.FromDays(7);

    private readonly IAppRepository _apps;
    private readonly IAppRoleRepository _roles;
    private readonly IAppUserRepository _appUsers;
    private readonly IUserRepository _users;
    private readonly ITenantRepository _tenants;
    private readonly IAuditRepository _audit;
    private readonly IAppAccessService _access;
    private readonly IQueryContext _identity;

    public QbUserProvisioning(IAppRepository apps, IAppRoleRepository roles, IAppUserRepository appUsers, IUserRepository users,
        ITenantRepository tenants, IAuditRepository audit, IAppAccessService access, IQueryContext identity)
    {
        _apps = apps;
        _roles = roles;
        _appUsers = appUsers;
        _users = users;
        _tenants = tenants;
        _audit = audit;
        _access = access;
        _identity = identity;
    }

    public async Task<Guid> ProvisionAsync(Guid appPublicId, string email, string firstName, string lastName, Guid? rolePublicId, CancellationToken ct)
    {
        await _access.RequirePermissionByAppPublicIdAsync(appPublicId, PermissionCodes.UsersInvite, ct);
        var appId = await _apps.GetIdByPublicIdAsync(appPublicId, ct);

        // Quickbase: "If roleid is omitted, the user's role is set to none" — so no role unless one is given.
        AppRoleDetail? role = null;
        if (rolePublicId is { } rid)
        {
            role = (await _roles.ListDetailsByAppIdAsync(appId, ct)).FirstOrDefault(r => r.PublicId == rid)
                ?? throw new QbXmlException(QbXmlErrors.INVALID_ROLE, "No such role.");
            await EnsureMayAssignAsync(appId, role, ct);
        }

        var existing = await _users.GetByEmailAsync(email, ct);
        // Quickbase provisions only people it does not know yet; an existing (or already provisioned) user is error 111.
        if (existing is not null)
            throw new QbXmlException(QbXmlErrors.USER_EXISTS, "A user already exists.", "User already exists");

        User user;
        if (existing is null)
        {
            var id = await _users.CreateAsync(new User
            {
                Email = email,
                HashedPassword = string.Empty,
                Name = $"{firstName} {lastName}".Trim(),
                FirstName = firstName,
                LastName = lastName,
                IsActive = false,
            }, ct: ct);
            user = await _users.GetByIdAsync(id, ct);
        }
        else user = existing; // still pending from an earlier invite/provision

        var tenantRoles = await _tenants.ListRolesAsync(ct);
        var tenantRole = (role is not null && role.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
                ? tenantRoles.FirstOrDefault(r => r.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
                : null)
            ?? tenantRoles.FirstOrDefault(r => r.IsDefault) ?? tenantRoles.FirstOrDefault()
            ?? throw new InvalidOperationException("No tenant roles found.");

        await _tenants.UpsertTenantUserAsync(new TenantUser
        {
            TenantId = _identity.TenantId,
            UserId = user.Id,
            TenantRoleId = tenantRole.Id,
            IsOwner = false,
            IsActive = false,
            InvitedBy = _identity.UserId,
        }, ct);

        // Pending app-role grant, recorded the same way the invite command records it. The secret is never sent anywhere:
        // API_SendInvitation later issues a fresh invitation (with its own link) through the existing invite command.
        var secret = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
        await _audit.CreateInviteTokenAsync(user.Id, _identity.TenantId, tenantRole.Id, secret, DateTime.UtcNow.Add(GRANT_LIFETIME),
            _identity.UserId, appId, role?.Id, ct);

        // Quickbase shows a provisioned user in the app's user list with their role straight away. Record the app-role
        // membership now, exactly as accept-invite would create it (that step skips a membership that already exists);
        // the person stays pending through their own inactive user/tenant records until they accept.
        if (role is not null && await _appUsers.GetByAppUserAndRoleAsync(appId, user.Id, role.Id, ct) is null)
            await _appUsers.CreateAsync(new AppUser
            {
                AppId = appId,
                UserId = user.Id,
                UserPublicId = user.PublicId,
                UserName = user.Name,
                UserEmail = user.Email,
                AppRoleId = role.Id,
                Status = "Active",
                AddedBy = _identity.UserId,
            }, ct: ct);

        await _audit.LogActivityAsync(AuditActions.Created, AuditEntityTypes.TenantUser, user.Id.ToString(),
            $"User provisioned to app (no invitation sent): {email}", appId: appId, ct: ct);
        return user.PublicId;
    }

    /// <summary>The invite command's rules: the target role must rank strictly below the caller's and be manageable by it.</summary>
    private async Task EnsureMayAssignAsync(long appId, AppRoleDetail target, CancellationToken ct)
    {
        if (_identity.IsSuperAdmin) return;
        var actorRolePublicId = await _appUsers.GetUserRolePublicIdAsync(appId, _identity.UserId, ct)
            ?? throw new QbXmlException(QbXmlErrors.INSUFFICIENT_PERMISSIONS, "Insufficient permissions", "Your role in this application was not found.");
        var actor = await _roles.GetByPublicIdAsync(actorRolePublicId, ct)
            ?? throw new QbXmlException(QbXmlErrors.INSUFFICIENT_PERMISSIONS, "Insufficient permissions", "Your role in this application was not found.");
        if ((target.Rank ?? int.MaxValue) <= (actor.Rank ?? int.MaxValue))
            throw new QbXmlException(QbXmlErrors.INSUFFICIENT_PERMISSIONS, "Insufficient permissions", "You cannot assign a role equal to or above your own.");
        if (actor.ManageableRolesType == "None")
            throw new QbXmlException(QbXmlErrors.INSUFFICIENT_PERMISSIONS, "Insufficient permissions", "Your role is not allowed to manage or assign any roles.");
        if (actor.ManageableRolesType == "Manual" && !(await _roles.GetManageableRolePublicIdsAsync(actor.Id, ct)).Contains(target.PublicId))
            throw new QbXmlException(QbXmlErrors.INSUFFICIENT_PERMISSIONS, "Insufficient permissions", "Your role is not allowed to assign this role.");
    }
}
