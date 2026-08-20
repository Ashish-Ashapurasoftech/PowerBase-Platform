using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppTokens.Commands.BulkDeleteAppTokens;

public class BulkDeleteAppTokensCommandHandler
{
    private readonly IAppTokenRepository _appTokenRepository;
    private readonly IAppRepository _appRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public BulkDeleteAppTokensCommandHandler(
        IAppTokenRepository appTokenRepository,
        IAppRepository appRepository,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appTokenRepository = appTokenRepository;
        _appRepository = appRepository;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<int> HandleAsync(BulkDeleteAppTokensCommand command, CancellationToken cancellationToken = default)
    {
        if (command.PublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = ["At least one app token ID is required."] });
        if (command.PublicIds.Count > 500)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = ["Cannot delete more than 500 app tokens at once."] });

        var appId = await _appRepository.GetIdByPublicIdAsync(command.AppPublicId, cancellationToken);
        if (appId == 0)
        {
            throw new NotFoundException("App", command.AppPublicId);
        }

        // Single UPDATE ... WHERE PublicId IN (...) statement (see AppTokenRepository.BulkDeleteAsync) —
        // not a loop of single deletes, so this is one DB round-trip for the whole selection.
        var deletedCount = await _appTokenRepository.BulkDeleteAsync(command.PublicIds, _queryContext.TenantId, command.AppPublicId, cancellationToken);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.AppToken,
            command.AppPublicId.ToString(),
            $"{deletedCount} app token(s) bulk-deleted.",
            appId: appId,
            ct: cancellationToken);

        return deletedCount;
    }
}
