using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.ListPipelines;

public class ListPipelinesQueryHandler
{
    private static readonly System.Collections.Generic.HashSet<string> AllowedSortFields =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "name",
            "description",
            "isActive",
            "createdOn"
        };

    private readonly IPipelineRepository _pipelineRepo;
    private readonly IQueryContext _queryContext;

    public ListPipelinesQueryHandler(IPipelineRepository pipelineRepo, IQueryContext queryContext)
    {
        _pipelineRepo = pipelineRepo;
        _queryContext = queryContext;
    }

    public async Task<PipelineListResult> HandleAsync(ListPipelinesQuery query, CancellationToken ct = default)
    {
        var validator = new ListPipelinesQueryValidator();
        var validation = await validator.ValidateAsync(query, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 10 : query.PageSize;
        var sortBy = AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "createdOn";

        var userId = _queryContext.UserId;
        var totalCount = await _pipelineRepo.CountByUserAsync(userId, query.Search, query.IsActive, ct);
        var pipelines = await _pipelineRepo.ListByUserPagedAsync(userId, page, pageSize, query.Search, sortBy, query.SortDesc, query.IsActive, ct);

        var items = pipelines.Select(p => new PipelineListItem(
            p.PublicId,
            p.Name,
            p.Description,
            p.IsActive,
            p.CreatedOn,
            p.FirstStepType,
            p.FirstStepSubtype
        )).ToList();

        return new PipelineListResult(items, totalCount, page, pageSize);
    }
}
