using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Apps.Queries.ListMyAppCopies;

/// <summary>The caller's copies with data for the app list: a copy goes on after its dialog is closed, so the list is
/// where its progress, then its result, can still be seen. Copies a server restart cut off are removed first (as the
/// next copy would do), so they show as failed instead of running for ever.</summary>
public class ListMyAppCopiesQueryHandler
{
    private readonly IAppCopyRepository _copyRepo;
    private readonly IQueryContext _queryContext;

    public ListMyAppCopiesQueryHandler(IAppCopyRepository copyRepo, IQueryContext queryContext)
    {
        _copyRepo = copyRepo;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<AppCopyRunSummary>> HandleAsync(CancellationToken ct = default)
    {
        try
        {
            await _copyRepo.DiscardStaleDataCopiesAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort: the list is still worth showing; the next copy or list call tries again.
        }
        return await _copyRepo.ListDataCopiesAsync(_queryContext.UserId, ct);
    }
}
