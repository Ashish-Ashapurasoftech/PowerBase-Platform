using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.InviteAppUser;

/// <summary>
/// Invites a user to a specific app within the current tenant.
///
/// Logic:
///   • User exists in PowerBase AND is active (IsActive = true)
///       → Ensure they are a tenant member (upsert as active if not already)
///       → Add them directly to the app
///       → Send an informational email (no setup required)
///
///   • User does NOT exist in PowerBase OR is pending (IsActive = false)
///       → Create the user record if new (IsActive = false)
///       → Add them to the tenant as inactive (pending invite acceptance)
///       → Create an invite token and send a setup email
///       → NOTE: app membership is granted automatically when they accept the invite
///         via the AcceptInvite flow (or the admin can manually add them afterwards)
/// </summary>
public class InviteAppUserCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IEmailService _emailService;
    private readonly IQueryContext _queryContext;

    public InviteAppUserCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IEmailService emailService,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _emailService = emailService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(InviteAppUserCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["Email"] = ["Email is required."] });

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        // Resolve app role
        AppRole targetRole;
        if (command.AppRolePublicId.HasValue)
        {
            targetRole = await _appRoleRepo.GetByPublicIdAsync(command.AppRolePublicId.Value, ct)
                ?? throw new NotFoundException("AppRole", command.AppRolePublicId.Value);
        }
        else
        {
            var defaultRoleId = await _appRepo.GetDefaultRoleIdAsync(appId, ct)
                ?? throw new NotFoundException("DefaultAppRole", appId);
            var roles = await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct);
            var defaultRoleDetail = roles.FirstOrDefault(r => r.Id == defaultRoleId)
                ?? throw new InvalidOperationException("Default app role not found.");
            targetRole = new AppRole 
            { 
                Id = defaultRoleDetail.Id, 
                PublicId = defaultRoleDetail.PublicId, 
                Name = defaultRoleDetail.Name,
                Rank = defaultRoleDetail.Rank,
                ManageableRolesType = defaultRoleDetail.ManageableRolesType
            };
        }

        if (!_queryContext.IsSuperAdmin)
        {
            var actorRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(appId, _queryContext.UserId, ct);
            if (!actorRolePublicId.HasValue)
            {
                throw new UnauthorizedActionException("Your role in this application was not found.");
            }

            var actorRole = await _appRoleRepo.GetByPublicIdAsync(actorRolePublicId.Value, ct);
            if (actorRole == null)
            {
                throw new UnauthorizedActionException("Your role in this application was not found.");
            }

            // Hard Rule: Target role's rank must be strictly greater than actor's rank
            int actorRank = actorRole.Rank ?? int.MaxValue;
            int targetRank = targetRole.Rank ?? int.MaxValue;
            if (targetRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot assign a role equal to or above your own.");
            }

            // Configured setting check
            if (actorRole.ManageableRolesType == "None")
            {
                throw new UnauthorizedActionException("Your role is not allowed to manage or assign any roles.");
            }
            else if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(targetRole.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to assign this role.");
                }
            }
        }

        long appRoleId = targetRole.Id;
        string appRoleName = targetRole.Name;

        var user = await _userRepo.GetByEmailAsync(command.Email, ct);
        bool isNewUser = user is null;
        bool isPendingUser = user is { IsActive: false };
        bool isExistingActiveUser = !isNewUser && !isPendingUser;

        var inviter = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var tenantName = await _tenantRepo.GetTenantNameByIdAsync(_queryContext.TenantId, ct) ?? "PowerBase";

        if (isExistingActiveUser)
        {
            // ── Path A: user already has an active PowerBase account ────────────
            // 1. Ensure they belong to this tenant
            bool isTenantMember = await _tenantRepo.IsActiveMemberAsync(user!.Id, ct);
            if (!isTenantMember)
            {
                // Find the default tenant role to assign
                var tenantRoles = await _tenantRepo.ListRolesAsync(ct);
                var defaultTenantRole = tenantRoles.FirstOrDefault(r => r.IsDefault)
                    ?? tenantRoles.FirstOrDefault()
                    ?? throw new InvalidOperationException("No tenant roles found.");

                var targetTenantRole = defaultTenantRole;
                if (string.Equals(appRoleName, "Administrator", StringComparison.OrdinalIgnoreCase))
                {
                    var adminRole = tenantRoles.FirstOrDefault(r => string.Equals(r.Name, "Administrator", StringComparison.OrdinalIgnoreCase));
                    if (adminRole != null)
                    {
                        targetTenantRole = adminRole;
                    }
                }

                await _tenantRepo.UpsertTenantUserAsync(new TenantUser
                {
                    TenantId     = _queryContext.TenantId,
                    UserId       = user.Id,
                    TenantRoleId = targetTenantRole.Id,
                    IsOwner      = false,
                    IsActive     = true,
                    InvitedBy    = _queryContext.UserId,
                }, ct);
            }

            // 2. Check if user already has this exact role in this app
            var existingSameRole = await _appUserRepo.GetByAppUserAndRoleAsync(appId, user.Id, appRoleId, ct);
            if (existingSameRole is not null)
            {
                throw new DuplicateException("AppUser", $"User '{command.Email}' already has the '{appRoleName}' role in this application.");
            }

            // 3. Add directly to the app
            await _appUserRepo.CreateAsync(new AppUser
            {
                AppId        = appId,
                UserId       = user.Id,
                UserPublicId = user.PublicId,
                UserName     = user.Name,
                UserEmail    = user.Email,
                AppRoleId    = appRoleId,
                Status       = "Active",
                AddedBy      = _queryContext.UserId,
            }, ct: ct);

            // 4. Send informational email — no setup needed
            await _emailService.SendInvitationEmailAsync(command.Email, tenantName, inviter.Name, ct);

            await _auditRepo.LogActivityAsync(
                AuditActions.Created, AuditEntityTypes.AppUser,
                user.Id.ToString(), $"User invited to app: {command.Email}", appId: appId, ct: ct);
        }
        else
        {
            // ── Path B: user is new or still pending ────────────────────────────
            // 1. Create user record if brand-new
            if (isNewUser)
            {
                var userId = await _userRepo.CreateAsync(new User
                {
                    Email          = command.Email,
                    HashedPassword = string.Empty,
                    Name           = command.Email.Split('@')[0],
                    IsActive       = false,
                }, ct: ct);
                user = await _userRepo.GetByIdAsync(userId, ct);
            }

            // 2. Resolve default tenant role for provisional membership
            var tenantRoles = await _tenantRepo.ListRolesAsync(ct);
            var defaultTenantRole = tenantRoles.FirstOrDefault(r => r.IsDefault)
                ?? tenantRoles.FirstOrDefault()
                ?? throw new InvalidOperationException("No tenant roles found.");

            var targetTenantRole = defaultTenantRole;
            if (string.Equals(appRoleName, "Administrator", StringComparison.OrdinalIgnoreCase))
            {
                var adminRole = tenantRoles.FirstOrDefault(r => string.Equals(r.Name, "Administrator", StringComparison.OrdinalIgnoreCase));
                if (adminRole != null)
                {
                    targetTenantRole = adminRole;
                }
            }

            // 3. Add to tenant as inactive (will activate when they accept the invite)
            await _tenantRepo.UpsertTenantUserAsync(new TenantUser
            {
                TenantId     = _queryContext.TenantId,
                UserId       = user!.Id,
                TenantRoleId = targetTenantRole.Id,
                IsOwner      = false,
                IsActive     = false,
                InvitedBy    = _queryContext.UserId,
            }, ct);

            // 4. Generate invite token and send setup email
            var rawToken  = Guid.NewGuid().ToString("N");
            var tokenHash = ComputeSha256(rawToken);
            await _auditRepo.CreateInviteTokenAsync(
                user.Id, _queryContext.TenantId, targetTenantRole.Id,
                tokenHash, DateTime.UtcNow.AddDays(7),
                _queryContext.UserId, appId, appRoleId, ct);

            var baseUrl   = command.FrontendBaseUrl.TrimEnd('/');
            var setupLink = $"{baseUrl}/auth/accept-invite?token={rawToken}";
            await _emailService.SendInviteSetupEmailAsync(command.Email, tenantName, inviter.Name, setupLink, ct);

            await _auditRepo.LogActivityAsync(
                AuditActions.InviteSent, AuditEntityTypes.TenantUser,
                user.Id.ToString(), $"User invited (setup email) to app: {command.Email}", appId: appId, ct: ct);
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
