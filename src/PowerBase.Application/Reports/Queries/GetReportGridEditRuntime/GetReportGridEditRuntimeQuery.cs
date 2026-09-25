using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Queries.GetReportGridEditRuntime;

public record GetReportGridEditRuntimeQuery(Guid ReportId);

/// <summary>What the report grid needs the moment Grid Edit is turned on: every applied rule with its
/// conditions, actions and element->field map, in the report's priority order — one request and one
/// database round trip, instead of a request per rule plus one per form layout.</summary>
public class GetReportGridEditRuntimeQueryHandler
{
    private readonly IReportRepository _reportRepo;

    public GetReportGridEditRuntimeQueryHandler(IReportRepository reportRepo) => _reportRepo = reportRepo;

    public Task<IReadOnlyList<GridEditRuntimeRule>> HandleAsync(GetReportGridEditRuntimeQuery query, CancellationToken ct = default)
        => _reportRepo.GetGridEditRuntimeAsync(query.ReportId, ct);
}
