using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Apps.Queries.GetAppCopyProgress;

/// <summary>How far the caller's running app copy is, for the copy dialog's progress bar. Null when this API instance
/// knows no copy under that id for this user (not started yet, ended long ago, or run by another instance).</summary>
public class GetAppCopyProgressQueryHandler
{
    private readonly IAppCopyProgressStore _store;
    private readonly IQueryContext _queryContext;

    public GetAppCopyProgressQueryHandler(IAppCopyProgressStore store, IQueryContext queryContext)
    {
        _store = store;
        _queryContext = queryContext;
    }

    public AppCopyProgress? Handle(Guid progressId) => _store.Get(progressId, _queryContext.UserId);
}
