using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Users.Commands.InviteUser;

public class InviteUserCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IEmailService _emailService;
    private readonly IQueryContext _queryContext;

    public InviteUserCommandHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IEmailService emailService,
        IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _emailService = emailService;
        _queryContext = queryContext;
    }

    public async Task<TenantUserDetail> HandleAsync(InviteUserCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["Email"] = ["Email is required."] });

        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.RolePublicId, ct)
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
        else if (await _tenantRepo.IsActiveMemberAsync(user!.Id, ct))
        {
            throw new DuplicateException("TenantUser", $"User with email '{command.Email}' is already a member of this tenant.");
        }

        // Pending users (IsActive=false) haven't accepted their invite yet — keep TenantUser inactive
        // Active existing users are immediately active in the new tenant
        bool tenantUserIsActive = !isNewUser && !isPendingUser;

        // UPSERT handles re-invite after removal (soft-deleted TenantUser violates UNIQUE constraint on plain INSERT)
        await _tenantRepo.UpsertTenantUserAsync(new TenantUser
        {
            TenantId = _queryContext.TenantId,
            UserId = user!.Id,
            TenantRoleId = role.Id,
            IsOwner = false,
            IsActive = tenantUserIsActive,
            InvitedBy = _queryContext.UserId,
        }, ct: ct);

        var inviter = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var tenantName = await _tenantRepo.GetTenantNameByIdAsync(_queryContext.TenantId, ct) ?? "PowerBase";

        if (isNewUser || isPendingUser)
        {
            var rawToken = Guid.NewGuid().ToString("N");
            var tokenHash = ComputeSha256(rawToken);
            await _auditRepo.CreateInviteTokenAsync(
                user.Id, _queryContext.TenantId, role.Id,
                tokenHash, DateTime.UtcNow.AddDays(7),
                _queryContext.UserId, ct: ct);

            var baseUrl = command.FrontendBaseUrl.TrimEnd('/');
            var setupLink = $"{baseUrl}/auth/accept-invite?token={rawToken}";
            await _emailService.SendInviteSetupEmailAsync(command.Email, tenantName, inviter.Name, setupLink, ct);
        }
        else
        {
            await _emailService.SendInvitationEmailAsync(command.Email, tenantName, inviter.Name, ct);
        }

        var users = await _tenantRepo.ListUsersAsync(ct: ct);
        var addedUser = users.First(u => u.UserPublicId == user.PublicId);

        await _auditRepo.LogActivityAsync(
            AuditActions.InviteSent, AuditEntityTypes.TenantUser, addedUser.UserPublicId.ToString(), $"User invited to tenant: {command.Email}", ct: ct);

        return addedUser;
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
