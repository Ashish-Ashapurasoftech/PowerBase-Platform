using System.Globalization;
using System.Xml.Linq;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.InviteAppUser;
using PowerBase.Application.Apps.Queries.ListAppUsers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// User / role Quickbase XML calls: API_GetUserInfo, API_GetUserRole, API_UserRoles, API_AddUserToRole,
/// API_ProvisionUser, API_SendInvitation. User and role ids are our PublicId Guids (user decision 2026-11-04).
/// Writes go through our existing app-user commands (role hierarchy checks included).
/// </summary>
public sealed class QbXmlUserActions
{
    private readonly IAppRepository _apps;
    private readonly IAppRoleRepository _roles;
    private readonly IUserRepository _users;
    private readonly ITenantRepository _tenants;
    private readonly IAppAccessService _access;
    private readonly IQueryContext _identity;
    private readonly IConfiguration _configuration;
    private readonly ListAppUsersQueryHandler _listUsers;
    private readonly AddAppUserCommandHandler _addUser;
    private readonly InviteAppUserCommandHandler _invite;
    private readonly QbUserProvisioning _provisioning;
    private readonly Common.QbIdResolver _ids;

    public QbXmlUserActions(IAppRepository apps, IAppRoleRepository roles, IUserRepository users, ITenantRepository tenants,
        IAppAccessService access, IQueryContext identity, IConfiguration configuration, ListAppUsersQueryHandler listUsers,
        AddAppUserCommandHandler addUser, InviteAppUserCommandHandler invite, QbUserProvisioning provisioning, Common.QbIdResolver ids)
    {
        _ids = ids;
        _provisioning = provisioning;
        _apps = apps;
        _roles = roles;
        _users = users;
        _tenants = tenants;
        _access = access;
        _identity = identity;
        _configuration = configuration;
        _listUsers = listUsers;
        _addUser = addUser;
        _invite = invite;
    }

    /// <summary>The app dbid: app Guid or numeric app id.</summary>
    private async Task<Guid> AppIdAsync(QbXmlRequest req, CancellationToken ct)
    {
        try { return await _ids.AppAsync(req.DbId, "dbid", ct); }
        catch (Common.QbApiException) { throw QbXmlErrors.UnknownDbid(); }
    }

    /// <summary>The userid parameter: user Guid or numeric user id (users of the caller's account only).</summary>
    private async Task<Guid> UserIdParamAsync(QbXmlRequest req, bool required, CancellationToken ct)
    {
        var raw = req.Get("userid");
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) throw new QbXmlException(QbXmlErrors.UNKNOWN_USER, "Unknown user");
            return Guid.Empty;
        }
        try { return await _ids.UserAsync(raw, "userid", ct); }
        catch (Common.QbApiException) { throw new QbXmlException(QbXmlErrors.UNKNOWN_USER, "Unknown user"); }
    }

    /// <summary>The roleid parameter: role Guid or numeric role id; it must be a role of this app.</summary>
    private async Task<Guid?> RoleIdParamAsync(QbXmlRequest req, Guid appPublicId, bool required, CancellationToken ct)
    {
        var raw = req.Get("roleid");
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) throw new QbXmlException(QbXmlErrors.INVALID_ROLE, "No such role.");
            return null;
        }
        var appId = await _apps.GetIdByPublicIdAsync(appPublicId, ct);
        Guid? roleId;
        try { roleId = await _ids.RoleAsync(raw, appId, "roleid", ct); }
        catch (Common.QbApiException) { throw new QbXmlException(QbXmlErrors.INVALID_ROLE, "No such role."); }
        if (roleId is { } rid && (await _roles.ListDetailsByAppIdAsync(appId, ct)).All(r => r.PublicId != rid))
            throw new QbXmlException(QbXmlErrors.INVALID_ROLE, "No such role.");
        return roleId;
    }

    /// <summary>Quickbase's answer to API_GetUserInfo for an e-mail it does not know.</summary>
    private static QbXmlException NoSuchUserByEmail() => new(QbXmlErrors.INVALID_INPUT, "Invalid input",
        "The user with the specified email address or user name does not exist.");

    private async Task<User> UserByPublicIdAsync(Guid id, CancellationToken ct)
    {
        try { return await _users.GetByPublicIdAsync(id, ct); }
        catch (NotFoundException) { throw new QbXmlException(QbXmlErrors.UNKNOWN_USER, "Unknown user"); }
    }

    /// <summary>Quickbase access level of a role: 1 Administrator, 2 Basic Access with Sharing, 3 Basic Access.</summary>
    public static (int Id, string Name) AccessLevel(AppRoleDetail? role)
    {
        if (role is null) return (3, "Basic Access");
        var perms = role.Permissions;
        if (role.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || perms.Contains(PermissionCodes.RolesManage))
            return (1, "Administrator");
        if (perms.Contains(PermissionCodes.UsersInvite) || perms.Contains(PermissionCodes.UsersManage))
            return (2, "Basic Access with Sharing");
        return (3, "Basic Access");
    }

    private async Task<IReadOnlyList<AppUserResult>> AllAppUsersAsync(Guid appId, CancellationToken ct)
    {
        var result = await _listUsers.HandleAsync(new ListAppUsersQuery(appId, 1, int.MaxValue, null, "userName", false, IsExport: true), ct);
        return result.Items;
    }

    private static XElement RoleElement(Guid roleId, string roleName, AppRoleDetail? detail, string? memberType = null, string? memberName = null)
    {
        var (accessId, accessName) = AccessLevel(detail);
        var role = new XElement("role", new XAttribute("id", roleId),
            new XElement("name", roleName),
            new XElement("access", new XAttribute("id", accessId), accessName));
        if (memberType != null) role.Add(new XElement("member", new XAttribute("type", memberType), memberName ?? ""));
        return role;
    }

    // ---------------------------------------------------------------- API_GetUserInfo

    public async Task<QbXmlResponse> GetUserInfoAsync(QbXmlRequest req, CancellationToken ct)
    {
        User user;
        var email = req.Get("email");
        if (string.IsNullOrWhiteSpace(email))
            user = await _users.GetByIdAsync(_identity.UserId, ct);
        else
        {
            user = await _users.GetByEmailAsync(email.Trim(), ct)
                ?? throw NoSuchUserByEmail();
            // Only people in the caller's account can be looked up.
            // Pending (provisioned, not yet accepted) members count too, as in Quickbase.
            if (user.Id != _identity.UserId && !await _tenants.IsActiveMemberAsync(user.Id, ct)
                && await _tenants.GetTenantUserByUserPublicIdAsync(user.PublicId, ct) is null)
                throw NoSuchUserByEmail();
        }
        return new QbXmlResponse(req.Action).Add(new XElement("user", new XAttribute("id", user.PublicId),
            new XElement("firstName", user.FirstName ?? ""),
            new XElement("lastName", user.LastName ?? ""),
            new XElement("login", user.Email),
            new XElement("email", user.Email),
            new XElement("screenName", user.Name),
            new XElement("isVerified", user.IsEmailVerified ? 1 : 0),
            new XElement("externalAuth", 0)));
    }

    // ---------------------------------------------------------------- API_GetUserRole

    public async Task<QbXmlResponse> GetUserRoleAsync(QbXmlRequest req, CancellationToken ct)
    {
        var appId = await AppIdAsync(req, ct);
        var userId = await UserIdParamAsync(req, required: false, ct);
        var me = await _users.GetByIdAsync(_identity.UserId, ct);
        if (userId == Guid.Empty) userId = me.PublicId;
        if (userId == me.PublicId) await _access.RequireMembershipByAppPublicIdAsync(appId, ct);
        else await _access.RequirePermissionByAppPublicIdAsync(appId, PermissionCodes.UsersManage, ct);

        var target = await UserByPublicIdAsync(userId, ct);
        var inclGroups = req.Flag("inclgrps");
        var rows = (await AllAppUsersAsync(appId, ct)).Where(r => r.UserPublicId == userId && (inclGroups || !r.IsFromGroup)).ToList();
        var details = (await _roles.ListDetailsByAppIdAsync(await _apps.GetIdByPublicIdAsync(appId, ct), ct)).ToDictionary(r => r.PublicId);

        var roles = new XElement("roles");
        foreach (var r in rows)
            roles.Add(RoleElement(r.RolePublicId, r.RoleName, details.GetValueOrDefault(r.RolePublicId),
                inclGroups ? (r.IsFromGroup ? "group" : "user") : null, r.IsFromGroup ? r.GroupName : target.Name));
        return new QbXmlResponse(req.Action).Add(new XElement("user", new XAttribute("id", target.PublicId),
            new XElement("name", target.Name), roles));
    }

    // ---------------------------------------------------------------- API_UserRoles

    public async Task<QbXmlResponse> UserRolesAsync(QbXmlRequest req, CancellationToken ct)
    {
        var appId = await AppIdAsync(req, ct);
        await _access.RequirePermissionByAppPublicIdAsync(appId, PermissionCodes.UsersManage, ct);
        var rows = await AllAppUsersAsync(appId, ct);
        var details = (await _roles.ListDetailsByAppIdAsync(await _apps.GetIdByPublicIdAsync(appId, ct), ct)).ToDictionary(r => r.PublicId);

        var users = new XElement("users");
        foreach (var group in rows.Where(r => !r.IsFromGroup).GroupBy(r => r.UserPublicId))
        {
            var user = await UserByPublicIdAsync(group.Key, ct);
            var roles = new XElement("roles");
            foreach (var r in group) roles.Add(RoleElement(r.RolePublicId, r.RoleName, details.GetValueOrDefault(r.RolePublicId)));
            users.Add(new XElement("user", new XAttribute("type", "user"), new XAttribute("id", user.PublicId),
                new XElement("name", user.Name),
                new XElement("lastAccess", user.LastLoginOn is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : ""),
                new XElement("lastAccessAppLocal", user.LastLoginOn?.ToString("MM-dd-yyyy hh:mm tt", CultureInfo.InvariantCulture) ?? ""),
                new XElement("firstName", user.FirstName ?? ""),
                new XElement("lastName", user.LastName ?? ""),
                roles));
        }
        foreach (var group in rows.Where(r => r.IsFromGroup && !string.IsNullOrEmpty(r.GroupName)).GroupBy(r => r.GroupName!))
        {
            var roles = new XElement("roles");
            foreach (var r in group.DistinctBy(r => r.RolePublicId)) roles.Add(RoleElement(r.RolePublicId, r.RoleName, details.GetValueOrDefault(r.RolePublicId)));
            users.Add(new XElement("user", new XAttribute("type", "group"), new XAttribute("id", ""), new XElement("name", group.Key), roles));
        }
        return new QbXmlResponse(req.Action).Add(users);
    }

    // ---------------------------------------------------------------- API_AddUserToRole

    public async Task<QbXmlResponse> AddUserToRoleAsync(QbXmlRequest req, CancellationToken ct)
    {
        var appId = await AppIdAsync(req, ct);
        await _access.RequireMembershipByAppPublicIdAsync(appId, ct); // also enforces an app-scoped user token
        var user = await UserByPublicIdAsync(await UserIdParamAsync(req, required: true, ct), ct);
        var roleId = (await RoleIdParamAsync(req, appId, required: true, ct))!.Value;
        try { await _addUser.HandleAsync(new AddAppUserCommand(appId, user.Email, roleId), ct); }
        catch (DuplicateException) { throw new QbXmlException(QbXmlErrors.USER_ALREADY_IN_ROLE, "User is already in specified role."); }
        catch (NotFoundException e) when (e.ErrorCode.Contains("ROLE", StringComparison.Ordinal))
        { _ = e; throw new QbXmlException(QbXmlErrors.INVALID_ROLE, "No such role."); }
        return new QbXmlResponse(req.Action);
    }

    // ---------------------------------------------------------------- API_ProvisionUser / API_SendInvitation

    /// <summary>Gives a person access to the app without e-mailing them (see <see cref="QbUserProvisioning"/>); returns their user id.</summary>
    public async Task<QbXmlResponse> ProvisionUserAsync(QbXmlRequest req, CancellationToken ct)
    {
        var appId = await AppIdAsync(req, ct);
        // Permission first, so a caller without the right gets "Insufficient permissions", not an input error.
        await _access.RequirePermissionByAppPublicIdAsync(appId, PermissionCodes.UsersInvite, ct);
        var email = req.Get("email")?.Trim();
        if (string.IsNullOrWhiteSpace(email)) throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "Missing or invalid email address.");
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('.', StringComparison.Ordinal))
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "Missing or invalid email address.");
        if (string.IsNullOrWhiteSpace(req.Get("fname")) || string.IsNullOrWhiteSpace(req.Get("lname")))
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "Missing 'fname' or 'lname' parameter");
        var roleId = await RoleIdParamAsync(req, appId, required: false, ct);
        var userId = await _provisioning.ProvisionAsync(appId, email, req.Get("fname")!.Trim(), req.Get("lname")!.Trim(), roleId, ct);
        return new QbXmlResponse(req.Action).Add("userid", userId);
    }

    public async Task<QbXmlResponse> SendInvitationAsync(QbXmlRequest req, CancellationToken ct)
    {
        var appId = await AppIdAsync(req, ct);
        await _access.RequirePermissionByAppPublicIdAsync(appId, PermissionCodes.UsersInvite, ct);
        var user = await UserByPublicIdAsync(await UserIdParamAsync(req, required: true, ct), ct);
        var current = (await AllAppUsersAsync(appId, ct)).FirstOrDefault(r => r.UserPublicId == user.PublicId && !r.IsFromGroup);
        try { await _invite.HandleAsync(new InviteAppUserCommand(appId, user.Email, current?.RolePublicId, FrontendBaseUrl()), ct); }
        catch (DuplicateException)
        {
            // Already an active member with this role: Quickbase's invitation is informational only, nothing left to grant.
        }
        return new QbXmlResponse(req.Action);
    }

    private string FrontendBaseUrl() => _configuration["Frontend:BaseUrl"] ?? "http://localhost:4200";
}
