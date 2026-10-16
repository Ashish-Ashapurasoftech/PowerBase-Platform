namespace PowerBase.Domain.Entities;

public class FormSectionBlock
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long FormSectionId { get; set; }
    public string? Heading { get; set; }
    /// <summary>Internal component name ("Column 1"). Kept when the heading is hidden so form rules can
    /// always reference the column by name. Null on columns created before migration 075 / by the importer.</summary>
    public string? Name { get; set; }
    /// <summary>Whether the column's heading is displayed. The displayed text is <see cref="Name"/>.</summary>
    public bool ShowHeading { get; set; } = true;
    public string? BackgroundColor { get; set; }
    public int? Width { get; set; }
    public int DisplayOrder { get; set; }
    public List<FormElement> Elements { get; set; } = [];

    // ── Grid-snap canvas (Phase 8) ──
    public int? ColStart { get; set; }
    public int? ColSpan { get; set; }
    public string? BackgroundType { get; set; }
    public string? BackgroundImage { get; set; }
    public string? DividerMode { get; set; }
    public string? DividerColor { get; set; }
    public int? DividerWidthPx { get; set; }
}
