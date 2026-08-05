using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.DeleteAppToken;

public class DeleteAppTokenCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;

    public DeleteAppTokenCommandHandler(IAppTokenRepository appTokenRepository, IQueryContext queryContext)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(Guid appPublicId, Guid publicId, CancellationToken cancellationToken = default)
    {
        var deleted = await _appTokenRepository.DeleteAsync(publicId, _queryContext.TenantId, appPublicId, cancellationToken);
        if (!deleted)
        {
            throw new NotFoundException("AppToken", publicId);
        }
    }
}
