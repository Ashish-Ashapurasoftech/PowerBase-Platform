namespace PowerBase.API.Models.Forms;

public class SaveFormLayoutRequest
{
    public List<FormSectionLayoutRequest> Sections { get; init; } = [];
}

public class FormSectionLayoutRequest
{
    public Guid? PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsCollapsed { get; init; }
    public List<FormBlockLayoutRequest> Blocks { get; init; } = [];
}

public class FormBlockLayoutRequest
{
    public Guid? PublicId { get; init; }
    public string? Heading { get; init; }
    public string? BackgroundColor { get; init; }
    public int? Width { get; init; }
    public List<FormElementLayoutRequest> Elements { get; init; } = [];
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
}
