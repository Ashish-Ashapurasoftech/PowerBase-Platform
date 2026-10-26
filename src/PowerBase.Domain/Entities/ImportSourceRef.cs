namespace PowerBase.Domain.Entities;

/// <summary>Links an element created by a QBL/PBL import to the logical ref it came from.</summary>
public class ImportSourceRef
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public string ElementType { get; set; } = string.Empty;
    public long ElementId { get; set; }
    public string SourceLogicalRef { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public static class ImportElementTypes
{
    public const string Table = "Table";
    public const string Field = "Field";
    public const string Relationship = "Relationship";
    public const string Report = "Report";
    public const string Form = "Form";
    public const string Role = "Role";
    public const string Variable = "Variable";
    public const string Page = "Page";
}
