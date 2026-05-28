namespace PowerBase.Domain.Entities;

public class FormSectionBlock
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long FormSectionId { get; set; }
    public string? Heading { get; set; }
    public string? BackgroundColor { get; set; }
    public int? Width { get; set; }
    public int DisplayOrder { get; set; }
    public List<FormElement> Elements { get; set; } = [];
}
