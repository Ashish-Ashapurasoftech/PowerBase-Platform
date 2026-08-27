using PowerBase.API.Models.Records;

namespace PowerBase.API.Models.Reports;

public class ReportRunResponse
{
    public IReadOnlyList<ReportColumnDto> Columns { get; init; } = [];
    public IReadOnlyList<RecordResponse> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool IsDataMasked { get; init; }
    /// <summary>Gauge charts only, and only when Chart.GaugeGoalType is "DataValue" — the live
    /// aggregate (GaugeGoalFieldId summarized by GaugeGoalFunction) computed from this run's
    /// rows, resolving what the goal actually is right now. Null for every other chart type,
    /// for a Fixed-goal Gauge (use Chart.GoalValue directly), or when the goal field isn't
    /// visible/configured.</summary>
    public decimal? ResolvedGaugeGoalValue { get; init; }
}

public class ReportColumnDto
{
    public long FieldId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
}
