namespace PowerBase.API.Models.Relationships;

public class CreateRelationshipRequest
{
    public Guid ParentTablePublicId { get; set; }
    public Guid ChildTablePublicId { get; set; }
    public string ReferenceFieldName { get; set; } = string.Empty;
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
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
}

public class SummarySpecRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string Function { get; set; } = "Count";
    public int? TargetFid { get; set; }
}
