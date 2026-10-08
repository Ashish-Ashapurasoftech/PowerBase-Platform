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
    /// <summary>Table reports only: grand-total aggregates per column (keyed by field id) over EVERY
    /// record matching the report's filters — not just this page's rows. Only columns that opted
    /// into a total/average row are present; empty when the report hides totals.</summary>
    public IReadOnlyDictionary<string, ReportColumnTotalDto> Totals { get; init; } = new Dictionary<string, ReportColumnTotalDto>();
}

public class ReportColumnTotalDto
{
    /// <summary>Plain sum (for a checkbox column: how many records are checked).</summary>
    public decimal Sum { get; init; }
    /// <summary>Records with a non-blank value — the average's denominator when blanks don't count as 0.</summary>
    public long NonBlankCount { get; init; }
    /// <summary>All records matching the report's filters — the average's denominator when blanks count as 0.</summary>
    public long RowCount { get; init; }
}

public class ReportColumnDto
{
    public long FieldId { get; init; }
    /// <summary>Unique per-column key for reading this column's value out of a row's fields —
    /// use this, not FieldId, to look up a row's value for this column (Summary/Chart reports
    /// can have several columns sharing the same FieldId, e.g. Sum and Avg of the same field).</summary>
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
}
