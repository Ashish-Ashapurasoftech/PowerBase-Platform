namespace PowerBase.Application.Relationships;

/// <summary>One selectable parent record for a Reference field picker: the value to submit when
/// this option is chosen (the parent row Id as text for the default key, or the parent's key-field
/// value for a Set-Key table — either way, exactly what's stored in the child's reference column)
/// and up to 3 display values.</summary>
public class ReferenceOption
{
    public string Id { get; set; } = string.Empty;
    public string? Value1 { get; set; }
    public string? Value2 { get; set; }
    public string? Value3 { get; set; }
}
