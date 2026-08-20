namespace PowerBase.Domain.Entities;

/// <summary>One page (tab or wizard step) of a multi-page form. Phase 8 —
/// new forms only; a form with no pages renders as a single page.</summary>
public class FormPage
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long FormId { get; set; }
    public string Heading { get; set; } = "Page";
    public int DisplayOrder { get; set; }
}
