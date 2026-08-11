using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus;

public class UpdateAppTokenStatusCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public UpdateAppTokenStatusCommandHandler(
        IAppTokenRepository appTokenRepository,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateAppTokenStatusCommand command, CancellationToken cancellationToken = default)
    {
        var existingToken = await _appTokenRepository.GetByPublicIdAsync(command.PublicId, _queryContext.TenantId, command.AppPublicId, cancellationToken);
        if (existingToken == null)
        {
            throw new NotFoundException("AppToken", command.PublicId);
        }

        var updated = await _appTokenRepository.UpdateStatusAsync(
            command.PublicId, _queryContext.TenantId, command.AppPublicId, command.IsActive, cancellationToken);

        if (!updated)
        {
            throw new NotFoundException("AppToken", command.PublicId);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.AppToken,
            existingToken.PublicId.ToString(),
            $"App token status changed to {(command.IsActive ? "Active" : "Inactive")}: {existingToken.TokenName}",
            appId: existingToken.AppId,
            ct: cancellationToken);
    }
}
