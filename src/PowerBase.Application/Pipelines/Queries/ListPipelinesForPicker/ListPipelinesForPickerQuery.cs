using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.ListPipelinesForPicker;

public record ListPipelinesForPickerQuery(Guid AppPublicId);

public record PipelinePickerItem(Guid PublicId, string Name);

/// <summary>Dedicated, unpaged id/name list of an app's PowerFlows for filter dropdowns —
/// deliberately its own endpoint instead of reusing the paginated pipelines list, which is
/// scoped by creator rather than by app and caps out at a page size meant for a data grid.</summary>
public class ListPipelinesForPickerQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPipelineRepository _pipelineRepo;

    public ListPipelinesForPickerQueryHandler(IAppRepository appRepo, IPipelineRepository pipelineRepo)
    {
        _appRepo = appRepo;
        _pipelineRepo = pipelineRepo;
    }

    public async Task<IReadOnlyList<PipelinePickerItem>> HandleAsync(ListPipelinesForPickerQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        if (appId <= 0)
            throw new NotFoundException("App", query.AppPublicId);

        var pipelines = await _pipelineRepo.ListNamesByAppIdAsync(appId, ct);
        return pipelines.Select(p => new PipelinePickerItem(p.PublicId, p.Name)).ToList();
    }
}
