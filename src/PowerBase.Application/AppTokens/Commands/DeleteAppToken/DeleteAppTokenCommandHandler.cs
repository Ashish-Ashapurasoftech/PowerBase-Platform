using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.DeleteAppToken;

public class DeleteAppTokenCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public DeleteAppTokenCommandHandler(
        IAppTokenRepository appTokenRepository,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(Guid appPublicId, Guid publicId, CancellationToken cancellationToken = default)
    {
        var existingToken = await _appTokenRepository.GetByPublicIdAsync(publicId, _queryContext.TenantId, appPublicId, cancellationToken);
        if (existingToken == null)
        {
            throw new NotFoundException("AppToken", publicId);
        }

        var deleted = await _appTokenRepository.DeleteAsync(publicId, _queryContext.TenantId, appPublicId, cancellationToken);
        if (!deleted)
        {
            throw new NotFoundException("AppToken", publicId);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.AppToken,
            existingToken.PublicId.ToString(),
            $"App token deleted: {existingToken.TokenName}",
            appId: existingToken.AppId,
            ct: cancellationToken);
    }
}
