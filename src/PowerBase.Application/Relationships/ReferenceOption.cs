namespace PowerBase.Application.Relationships;

/// <summary>One selectable parent record for a Reference field picker: the parent row Id
/// (stored in the child's reference column) and up to 3 display values.</summary>
public class ReferenceOption
{
    public long Id { get; set; }
    public string? Value1 { get; set; }
    public string? Value2 { get; set; }
    public string? Value3 { get; set; }
}
