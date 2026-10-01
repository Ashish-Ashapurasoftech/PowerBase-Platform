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
    /// <summary>Count | Exists | Sum | Avg | Min | Max | DistinctCount | CombinedText.</summary>
    public string Function { get; set; } = "Count";
    /// <summary>Child field Fid to aggregate; required for every function except Count/Exists.</summary>
    public int? TargetFid { get; set; }
    /// <summary>Optional "matching criteria": only summarize child records matching this filter.</summary>
    public FilterGroup? MatchingCriteria { get; set; }

    // ── Combined Text only (ignored for other functions) ──

    /// <summary>Separator between values, e.g. ", ", "\n", " | " (max 10 chars). Null ⇒ ", ".</summary>
    public string? Delimiter { get; set; }
    /// <summary>Child field Fid to order values by; null ⇒ record creation order.</summary>
    public int? SortFid { get; set; }
    /// <summary>Reverse the order (by SortFid, or newest record first when unset).</summary>
    public bool SortDescending { get; set; }
    /// <summary>Include each unique value only once.</summary>
    public bool DistinctValues { get; set; }
}

/// <summary>Edit an existing summary field: same shape as <see cref="AddSummaryFieldRequest"/>, plus
/// an optional reason recorded on the field's version history.</summary>
public class UpdateSummaryFieldRequest : AddSummaryFieldRequest
{
    /// <summary>Why the summary changed (shown in the field's Version History). Optional.</summary>
    public string? CommitMessage { get; set; }
}

/// <summary>Change an existing relationship's display key override (scoped to this relationship only).</summary>
public class UpdateDisplayKeyRequest
{
    /// <summary>Parent field Fid to use as the picker/grid/filter label; 3 or null reverts to Standard key.</summary>
    public int? DisplayKeyFieldFid { get; set; }
}

/// <summary>Replace the dependent-dropdown conditions of a relationship's Reference field.</summary>
public class UpdateReferenceFilterRequest
{
    public List<ReferenceFilterConditionRequest> Conditions { get; set; } = new();
}

/// <summary>Junction mode when <see cref="JunctionTableId"/> is set; otherwise direct (<see cref="ParentFid"/>).</summary>
public class ReferenceFilterConditionRequest
{
    /// <summary>Fid of the field on this (child) table whose form value drives the filter.</summary>
    public int FormFid { get; set; }
    public int? ParentFid { get; set; }
    public Guid? JunctionTableId { get; set; }
    public int? JunctionParentFid { get; set; }
    public int? JunctionValueFid { get; set; }
}
