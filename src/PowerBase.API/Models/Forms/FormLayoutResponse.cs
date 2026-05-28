namespace PowerBase.API.Models.Forms;

public class FormLayoutResponse
{
    public Guid FormId { get; init; }
    public List<FormSectionResponse> Sections { get; init; } = [];
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
    public List<FormElementResponse> Elements { get; init; } = [];
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
}
