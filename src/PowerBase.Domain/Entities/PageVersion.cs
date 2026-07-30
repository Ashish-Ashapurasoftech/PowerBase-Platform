namespace PowerBase.Domain.Entities;

/// <summary>
/// An append-only snapshot of a Page, taken immediately before each edit (so version N is
/// always "what the page looked like before edit N" — what a restore needs). Enforced
/// append-only at the DB level by meta.TR_PageVersion_AppendOnly.
/// </summary>
public class PageVersion
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PageId { get; set; }
    public int VersionNo { get; set; }
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Definition { get; set; } = "{}";
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public string ChangeNotes { get; set; } = string.Empty;
    public bool WasPublished { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
}
