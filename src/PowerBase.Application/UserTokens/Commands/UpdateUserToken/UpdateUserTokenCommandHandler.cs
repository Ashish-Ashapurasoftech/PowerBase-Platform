using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserToken;

public class UpdateUserTokenCommandHandler
{
    private readonly IUserTokenRepository _userTokenRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public UpdateUserTokenCommandHandler(
        IUserTokenRepository userTokenRepository,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _userTokenRepository = userTokenRepository;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<bool> HandleAsync(UpdateUserTokenCommand command, CancellationToken cancellationToken = default)
    {
        var existingToken = await _userTokenRepository.GetByPublicIdAsync(command.PublicId, _queryContext.TenantId, cancellationToken);
        if (existingToken == null || existingToken.UserId != _queryContext.UserId)
        {
            throw new NotFoundException("UserToken", command.PublicId);
        }

        var oldValues = new Dictionary<string, object>
        {
            { "TokenName", existingToken.TokenName },
            { "Description", existingToken.Description ?? string.Empty },
            { "AccessAllApps", existingToken.AccessAllApps }
        };

        var result = await _userTokenRepository.UpdateDetailsAsync(
            existingToken.Id,
            command.TokenName,
            command.Description,
            command.AccessAllApps,
            command.AllowedAppPublicIds,
            cancellationToken
        );

        if (result)
        {
            var newValues = new Dictionary<string, object>
            {
                { "TokenName", command.TokenName },
                { "Description", command.Description ?? string.Empty },
                { "AccessAllApps", command.AccessAllApps }
            };

            await _auditRepo.LogActivityAsync(
                AuditActions.Updated,
                AuditEntityTypes.UserToken,
                existingToken.PublicId.ToString(),
                $"User token updated: {command.TokenName}",
                oldValues: JsonSerializer.Serialize(oldValues),
                newValues: JsonSerializer.Serialize(newValues),
                ct: cancellationToken);
        }

        return result;
    }
}
