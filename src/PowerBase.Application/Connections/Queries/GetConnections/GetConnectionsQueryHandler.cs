using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;

namespace PowerBase.Application.Connections.Queries.GetConnections;

/// <summary>
/// Returns display-safe rows only. The raw user token is never persisted, so it can never
/// be returned here; only the masked prefix travels to the client.
/// </summary>
public class GetConnectionsQueryHandler
{
    private readonly IPipelineAccountRepository _accountRepo;
    private readonly IQueryContext _queryContext;

    public GetConnectionsQueryHandler(
        IPipelineAccountRepository accountRepo,
        IQueryContext queryContext)
    {
        _accountRepo = accountRepo;
        _queryContext = queryContext;
    }

    public async Task<GetConnectionsResult> HandleAsync(GetConnectionsQuery query, CancellationToken ct = default)
    {
        var accounts = await _accountRepo.ListForUserAsync(_queryContext.UserId, ct);

        return new GetConnectionsResult
        {
            Items = accounts.Select(ConnectionScopeResolver.ToDto).ToList()
        };
    }
}
