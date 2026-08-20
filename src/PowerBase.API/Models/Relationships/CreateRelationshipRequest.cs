namespace PowerBase.API.Models.Relationships;

public class CreateRelationshipRequest
{
    public Guid ParentTablePublicId { get; set; }
    public Guid ChildTablePublicId { get; set; }
    /// <summary>Required when ReferenceFieldFid is null (creating a new reference field). Its Name is auto-generated.</summary>
    public string? ReferenceFieldLabel { get; set; }
    public bool IsReferenceRequired { get; set; }
    /// <summary>null ⇒ create a new Reference field; set ⇒ convert the existing child field with this Fid into the reference.</summary>
    public int? ReferenceFieldFid { get; set; }
    public List<LookupSpecRequest> Lookups { get; set; } = new();
    public List<SummarySpecRequest> Summaries { get; set; } = new();
}

public class LookupSpecRequest
{
    public int SourceFid { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class SummarySpecRequest
{
    public string Label { get; set; } = string.Empty;
    public string Function { get; set; } = "Count";
    public int? TargetFid { get; set; }
}
