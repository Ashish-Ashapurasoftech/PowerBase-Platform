namespace PowerBase.API.Models.Forms;

public class FormLayoutResponse
{
    public Guid FormId { get; init; }
    public List<FormSectionResponse> Sections { get; init; } = [];
    public List<FormPageResponse> Pages { get; init; } = [];
    public string PageNavMode { get; init; } = "tabs";
    public bool AlwaysTabsOnView { get; init; } = true;
    public string? ThemeJson { get; init; }
}

public class FormPageResponse
{
    public long DbId { get; init; }
    public Guid Id { get; init; }
    public string Heading { get; init; } = "Page";
    public int DisplayOrder { get; init; }
}

public class FormSectionResponse
{
    public long DbId { get; init; }
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ColumnCount { get; init; }
    public string? ColumnWidths { get; init; }
    public bool IsCollapsed { get; init; }
    public int DisplayOrder { get; init; }
    public List<FormBlockResponse> Blocks { get; init; } = [];
    public int GridCols { get; init; } = 12;
    public Guid? PageId { get; init; }
    public bool IsPinned { get; init; }
    public string? BackgroundColor { get; init; }
    public string? BackgroundType { get; init; }
    public string? BackgroundImage { get; init; }
    public string? BorderColor { get; init; }
    public int? BorderWidth { get; init; }
    public bool ShowDividers { get; init; } = true;
    public string? DividerColor { get; init; }
    public int? DividerWidthPx { get; init; }
}

public class FormBlockResponse
{
    public long DbId { get; init; }
    public Guid Id { get; init; }
    public string? Heading { get; init; }
    public string? BackgroundColor { get; init; }
    public int? Width { get; init; }
    public int DisplayOrder { get; init; }
    public List<FormElementResponse> Elements { get; init; } = [];
    public int? ColStart { get; init; }
    public int? ColSpan { get; init; }
    public string? BackgroundType { get; init; }
    public string? BackgroundImage { get; init; }
    public string? DividerMode { get; init; }
    public string? DividerColor { get; init; }
    public int? DividerWidthPx { get; init; }
}

public class FormElementResponse
{
    public long DbId { get; init; }
    public Guid Id { get; init; }
    public long? AppFieldId { get; init; }
    public string ElementType { get; init; } = "Field";
    public string? ElementContent { get; init; }
    public string LabelMode { get; init; } = string.Empty;
    public string? CustomLabel { get; init; }
    public bool ShowOnAdd { get; init; }
    public bool ShowOnEdit { get; init; }
    public bool ShowOnView { get; init; }
    public string WidthMode { get; init; } = string.Empty;
    public int? WidthValue { get; init; }
    public string? HelpTextOverride { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsRequired { get; init; }
    public string? DisplayAs { get; init; }
    public int DisplayOrder { get; init; }
    public int? ColStart { get; init; }
    public int? RowStart { get; init; }
    public int? ColSpan { get; init; }
    public int? RowSpan { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? CloneGroupId { get; init; }
    public Guid? PageId { get; init; }
    public string? TextStyle { get; init; }
    public string? BackgroundColor { get; init; }
    public string? BorderColor { get; init; }
    public int? BorderWidth { get; init; }
    public string? ContentWidthMode { get; init; }
    public int? ContentWidthValue { get; init; }
    public string? ContentWidthUnit { get; init; }
}
