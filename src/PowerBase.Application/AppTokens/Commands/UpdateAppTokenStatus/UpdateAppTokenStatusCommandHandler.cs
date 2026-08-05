using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus;

public class UpdateAppTokenStatusCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IQueryContext _queryContext;

    public UpdateAppTokenStatusCommandHandler(IAppTokenRepository appTokenRepository, IQueryContext queryContext)
    {
        _appTokenRepository = appTokenRepository;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(UpdateAppTokenStatusCommand command, CancellationToken cancellationToken = default)
    {
        var updated = await _appTokenRepository.UpdateStatusAsync(
            command.PublicId, _queryContext.TenantId, command.AppPublicId, command.IsActive, cancellationToken);

        if (!updated)
        {
            throw new NotFoundException("AppToken", command.PublicId);
        }
    }
}
