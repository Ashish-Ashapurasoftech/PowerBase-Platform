namespace PowerBase.API.Models.Forms;

public class SaveFormLayoutRequest
{
    public List<FormSectionLayoutRequest> Sections { get; init; } = [];
    // ── Grid-snap canvas (Phase 8) — omitted entirely by a pre-Phase-8 client. ──
    public List<FormPageLayoutRequest>? Pages { get; init; }
    public string? PageNavMode { get; init; }
    public bool? AlwaysTabsOnView { get; init; }
    public string? ThemeJson { get; init; }
}

public class FormPageLayoutRequest
{
    public Guid? PublicId { get; init; }
    public string Heading { get; init; } = "Page";
    public int DisplayOrder { get; init; }
}

public class FormSectionLayoutRequest
{
    public Guid? PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsCollapsed { get; init; }
    public List<FormBlockLayoutRequest> Blocks { get; init; } = [];
    public int? GridCols { get; init; }
    public Guid? PagePublicId { get; init; }
    public bool? IsPinned { get; init; }
    public string? BackgroundColor { get; init; }
    public string? BackgroundType { get; init; }
    public string? BackgroundImage { get; init; }
    public string? BorderColor { get; init; }
    public int? BorderWidth { get; init; }
    public bool? ShowDividers { get; init; }
    public string? DividerColor { get; init; }
    public int? DividerWidthPx { get; init; }
}

public class FormBlockLayoutRequest
{
    public Guid? PublicId { get; init; }
    public string? Heading { get; init; }
    public string? BackgroundColor { get; init; }
    public int? Width { get; init; }
    public List<FormElementLayoutRequest> Elements { get; init; } = [];
    public int? ColStart { get; init; }
    public int? ColSpan { get; init; }
    public string? BackgroundType { get; init; }
    public string? BackgroundImage { get; init; }
    public string? DividerMode { get; init; }
    public string? DividerColor { get; init; }
    public int? DividerWidthPx { get; init; }
}

public class FormElementLayoutRequest
{
    public Guid? PublicId { get; init; }
    public long? AppFieldId { get; init; }
    public string ElementType { get; init; } = "Field";
    public string? ElementContent { get; init; }
    public string LabelMode { get; init; } = "Default";
    public string? CustomLabel { get; init; }
    public bool ShowOnAdd { get; init; } = true;
    public bool ShowOnEdit { get; init; } = true;
    public bool ShowOnView { get; init; } = true;
    public string WidthMode { get; init; } = "Auto";
    public int? WidthValue { get; init; }
    public string? HelpTextOverride { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsRequired { get; init; }
    public string? DisplayAs { get; init; }
    public int? ColStart { get; init; }
    public int? RowStart { get; init; }
    public int? ColSpan { get; init; }
    public int? RowSpan { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? CloneGroupId { get; init; }
    public Guid? PagePublicId { get; init; }
    public string? TextStyle { get; init; }
    public string? BackgroundColor { get; init; }
    public string? BorderColor { get; init; }
    public int? BorderWidth { get; init; }
    public string? ContentWidthMode { get; init; }
    public int? ContentWidthValue { get; init; }
    public string? ContentWidthUnit { get; init; }
}
