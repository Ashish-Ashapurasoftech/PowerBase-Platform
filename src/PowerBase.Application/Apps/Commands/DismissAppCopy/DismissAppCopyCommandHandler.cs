using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Apps.Commands.DismissAppCopy;

/// <summary>Hides one of the caller's finished or failed copies from the app list (by the copy's app PublicId). A running
/// copy, or someone else's, is left as it is.</summary>
public class DismissAppCopyCommandHandler
{
    private readonly IAppCopyRepository _copyRepo;
    private readonly IQueryContext _queryContext;

    public DismissAppCopyCommandHandler(IAppCopyRepository copyRepo, IQueryContext queryContext)
    {
        _copyRepo = copyRepo;
        _queryContext = queryContext;
    }

    public Task HandleAsync(Guid copyAppPublicId, CancellationToken ct = default)
        => _copyRepo.DismissDataCopyAsync(copyAppPublicId, _queryContext.UserId, ct);
}
