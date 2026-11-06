using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Apps.Queries.GetAppCopyProgress;

/// <summary>How far the caller's running app copy is, for the copy dialog's progress bar. A copy with data is read from
/// the database (any API instance can answer, also after the copy request itself was cut by the hosting timeout); otherwise
/// from this API instance's memory. Null when there is no copy under that id for this user (not started yet, ended long ago,
/// or a copy without data run by another instance).</summary>
public class GetAppCopyProgressQueryHandler
{
    private readonly IAppCopyProgressStore _store;
    private readonly IAppCopyRepository _copyRepo;
    private readonly IQueryContext _queryContext;

    public GetAppCopyProgressQueryHandler(IAppCopyProgressStore store, IAppCopyRepository copyRepo, IQueryContext queryContext)
    {
        _store = store;
        _copyRepo = copyRepo;
        _queryContext = queryContext;
    }

    public async Task<AppCopyProgress?> HandleAsync(Guid progressId, CancellationToken ct = default)
        => await _copyRepo.GetDataCopyProgressAsync(progressId, _queryContext.UserId, ct)
           ?? _store.Get(progressId, _queryContext.UserId);
}
