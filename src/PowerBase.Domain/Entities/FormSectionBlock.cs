namespace PowerBase.Domain.Entities;

public class FormSectionBlock
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long FormSectionId { get; set; }
    public string? Heading { get; set; }
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
