using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Admin.Commands;

/// <summary>
/// Invites a user to the PowerBase platform without assigning them to any tenant.
/// The user receives a setup email and can set up their account; a SuperAdmin
/// can later assign them to tenants from the admin panel.
/// </summary>
public record AdminInvitePlatformUserCommand(
    string Email,
    string FrontendBaseUrl,
    long InvitedByUserId);

public class AdminInvitePlatformUserCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IEmailService _emailService;

    public AdminInvitePlatformUserCommandHandler(
        IUserRepository userRepo,
        IAuditRepository auditRepo,
        IEmailService emailService)
    {
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _emailService = emailService;
    }

    public async Task HandleAsync(AdminInvitePlatformUserCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["Email"] = ["Email is required."] });

        var existing = await _userRepo.GetByEmailAsync(command.Email, ct);
        if (existing is { IsActive: true })
            throw new DuplicateException("User", $"A user with email '{command.Email}' is already registered.");

        User user;
        if (existing is null)
        {
            var userId = await _userRepo.CreateAsync(new User
            {
                Email = command.Email,
                HashedPassword = string.Empty,
                Name = command.Email.Split('@')[0],
                IsActive = false,
            }, ct: ct);
            user = (await _userRepo.GetByIdAsync(userId, ct))!;
        }
        else
        {
            user = existing; // inactive / pending — re-send invite
        }

        var inviter = await _userRepo.GetByIdAsync(command.InvitedByUserId, ct);

        var rawToken = Guid.NewGuid().ToString("N");
        var tokenHash = ComputeSha256(rawToken);
        // TenantId and TenantRoleId are null — platform invite with no tenant pre-assigned
        await _auditRepo.CreateInviteTokenAsync(
            user.Id, tenantId: null, tenantRoleId: null,
            tokenHash, DateTime.UtcNow.AddDays(7),
            command.InvitedByUserId, ct: ct);

        var baseUrl = command.FrontendBaseUrl.TrimEnd('/');
        var setupLink = $"{baseUrl}/auth/accept-invite?token={rawToken}";
        await _emailService.SendInviteSetupEmailAsync(command.Email, "PowerBase", inviter.Name, setupLink, ct);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
