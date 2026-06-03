namespace PowerBase.Domain.Entities;

public class FormSection
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long FormId { get; set; }
    public string Name { get; set; } = "Section heading";
    public int ColumnCount { get; set; } = 2;
    public bool IsCollapsed { get; set; }
    public int DisplayOrder { get; set; }
    public string? ColumnWidths { get; set; }
    public List<FormElement> Elements { get; set; } = new();   // kept for backward-compat; populated from Blocks by the repo
    public List<FormSectionBlock> Blocks { get; set; } = [];
}
