using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Admin.Commands;

/// <summary>
/// Invites (or directly assigns) a user to a specific tenant from the admin panel.
/// Uses explicit tenantId — no QueryContext.TenantId dependency.
/// </summary>
public record AdminInviteUserCommand(
    long TenantId,
    string Email,
    Guid RolePublicId,
    string FrontendBaseUrl,
    long InvitedByUserId);

public class AdminInviteUserCommandHandler
{
    private readonly IAdminRepository _adminRepo;
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IEmailService _emailService;

    public AdminInviteUserCommandHandler(
        IAdminRepository adminRepo,
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IEmailService emailService)
    {
        _adminRepo = adminRepo;
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _emailService = emailService;
    }

    public async Task HandleAsync(AdminInviteUserCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["Email"] = ["Email is required."] });

        var roleId = await _adminRepo.GetTenantRoleIdByPublicIdAsync(command.TenantId, command.RolePublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.RolePublicId);

        var user = await _userRepo.GetByEmailAsync(command.Email, ct);
        bool isNewUser = user is null;
        bool isPendingUser = user is { IsActive: false };

        if (isNewUser)
        {
            var userId = await _userRepo.CreateAsync(new User
            {
                Email = command.Email,
                HashedPassword = string.Empty,
                Name = command.Email.Split('@')[0],
                IsActive = false,
            }, ct: ct);
            user = await _userRepo.GetByIdAsync(userId, ct);
        }
        else
        {
            // Check if already an active member of this specific tenant
            var existing = await _adminRepo.ListTenantMembersAsync(command.TenantId, ct);
            if (existing.Any(m => m.Email.Equals(command.Email, StringComparison.OrdinalIgnoreCase) && m.IsActive))
                throw new DuplicateException("TenantUser", $"User with email '{command.Email}' is already a member of this tenant.");
        }

        // Pending/new users are assigned as inactive — they activate when they accept the invite link
        bool tenantUserIsActive = !isNewUser && !isPendingUser;

        await _adminRepo.AssignUserToTenantAsync(command.TenantId, user!.Id, roleId, command.InvitedByUserId, tenantUserIsActive, ct);

        var inviter = await _userRepo.GetByIdAsync(command.InvitedByUserId, ct);
        var tenantName = await _tenantRepo.GetTenantNameByIdAsync(command.TenantId, ct) ?? "PowerBase";

        if (isNewUser || isPendingUser)
        {
            var rawToken = Guid.NewGuid().ToString("N");
            var tokenHash = ComputeSha256(rawToken);
            await _auditRepo.CreateInviteTokenAsync(
                user!.Id, command.TenantId, roleId,
                tokenHash, DateTime.UtcNow.AddDays(7),
                command.InvitedByUserId, ct: ct);

            var baseUrl = command.FrontendBaseUrl.TrimEnd('/');
            var setupLink = $"{baseUrl}/auth/accept-invite?token={rawToken}";
            await _emailService.SendInviteSetupEmailAsync(command.Email, tenantName, inviter.Name, setupLink, ct);
        }
        else
        {
            await _emailService.SendInvitationEmailAsync(command.Email, tenantName, inviter.Name, ct);
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
