using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Users.Commands.RemoveUser;

public class RemoveUserCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;

    public RemoveUserCommandHandler(ITenantRepository tenantRepo, IQueryContext queryContext)
    {
        _tenantRepo = tenantRepo;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(RemoveUserCommand command, CancellationToken ct = default)
    {
        var tenantUser = await _tenantRepo.GetTenantUserByUserPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("TenantUser", command.UserPublicId);

        if (tenantUser.UserId == _queryContext.UserId)
            throw new ValidationException(new Dictionary<string, string[]> { ["UserPublicId"] = ["You cannot remove yourself from the tenant."] });

        await _tenantRepo.RemoveTenantUserAsync(tenantUser.Id, ct);
    }
}
