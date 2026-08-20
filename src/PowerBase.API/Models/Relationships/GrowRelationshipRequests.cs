using PowerBase.Application.Reports;

namespace PowerBase.API.Models.Relationships;

/// <summary>Add lookup fields to an existing relationship's child table.</summary>
public class AddLookupFieldsRequest
{
    public List<LookupSpecRequest> Lookups { get; set; } = new();
}

/// <summary>Add a summary field to an existing relationship's parent table.</summary>
public class AddSummaryFieldRequest
{
    public string Label { get; set; } = string.Empty;
    /// <summary>Count | Exists | Sum | Avg | Min | Max.</summary>
    public string Function { get; set; } = "Count";
    /// <summary>Child field Fid to aggregate; required for Sum/Avg/Min/Max, null for Count/Exists.</summary>
    public int? TargetFid { get; set; }
    /// <summary>Optional "matching criteria": only summarize child records matching this filter.</summary>
    public FilterGroup? MatchingCriteria { get; set; }
}
