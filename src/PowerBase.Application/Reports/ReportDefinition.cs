namespace PowerBase.Application.Reports;

public class ReportDefinition
{
    public List<long> Columns { get; set; } = [];
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
    public List<ReportFilter> Filters { get; set; } = [];
    // Summary-only
    public long? GroupByFieldId { get; set; }
    /// <summary>EqualValues (default), FirstWord, FirstLetter</summary>
    public string GroupByMode { get; set; } = "EqualValues";
    public bool HideTotals { get; set; }
    public List<SummaryAggregation> Aggregations { get; set; } = [];

    // Dynamic Filters
    public string DynamicFilterType { get; set; } = "Default"; // Default, Custom, None
    public List<long> CustomDynamicFilterFields { get; set; } = [];
    public bool AllowQuickSearch { get; set; } = true;
}

public class ReportFilter
{
    public long FieldId { get; set; }
    /// <summary>eq, ne, contains, startsWith, gt, gte, lt, lte</summary>
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
}

public class SummaryAggregation
{
    public long FieldId { get; set; }
    /// <summary>Sum, Avg, Min, Max</summary>
    public string Function { get; set; } = "Sum";
    /// <summary>Normal (default) or PercentOfColumnTotal</summary>
    public string DisplayAs { get; set; } = "Normal";
}
