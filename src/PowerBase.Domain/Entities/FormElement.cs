namespace PowerBase.Domain.Entities;

public class FormElement
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long FormSectionId { get; set; }
    public long? FormSectionBlockId { get; set; }
    public long? AppFieldId { get; set; }
    public string ElementType { get; set; } = "Field";
    public string? ElementContent { get; set; }
    public string LabelMode { get; set; } = "Default";
    public string? CustomLabel { get; set; }
    public bool ShowOnAdd { get; set; } = true;
    public bool ShowOnEdit { get; set; } = true;
    public bool ShowOnView { get; set; } = true;
    public string WidthMode { get; set; } = "Auto";
    public int? WidthValue { get; set; }
    public string? HelpTextOverride { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsRequired { get; set; }
    public string? DisplayAs { get; set; }
    public int DisplayOrder { get; set; }
}
