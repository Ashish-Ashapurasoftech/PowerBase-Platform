using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Users.Commands.InviteUser;

public class InviteUserCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IEmailService _emailService;
    private readonly IQueryContext _queryContext;

    public InviteUserCommandHandler(
        IUserRepository userRepo,
        ITenantRepository tenantRepo,
        IEmailService emailService,
        IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _tenantRepo = tenantRepo;
        _emailService = emailService;
        _queryContext = queryContext;
    }

    public async Task<TenantUserDetail> HandleAsync(InviteUserCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
            throw new ValidationException(new Dictionary<string, string[]> { ["Email"] = ["Email is required."] });

        var user = await _userRepo.GetByEmailAsync(command.Email, ct)
            ?? throw new NotFoundException("User", command.Email);

        if (await _tenantRepo.IsUserInTenantAsync(user.Id, ct))
            throw new DuplicateException("TenantUser", "email", command.Email);

        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.RolePublicId);

        await _tenantRepo.CreateTenantUserAsync(new TenantUser
        {
            TenantId = _queryContext.TenantId,
            UserId = user.Id,
            TenantRoleId = role.Id,
            IsOwner = false,
            IsActive = true,
            InvitedBy = _queryContext.UserId,
        }, ct: ct);

        var inviter = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var tenantName = await _tenantRepo.GetTenantNameByIdAsync(_queryContext.TenantId, ct) ?? "PowerBase";
        await _emailService.SendInvitationEmailAsync(command.Email, tenantName, inviter.Name, ct);

        var users = await _tenantRepo.ListUsersAsync(ct);
        return users.First(u => u.UserPublicId == user.PublicId);
    }
}
