namespace PowerBase.Application.Relationships;

/// <summary>One selectable parent record for a Reference field picker: the value to submit when
/// this option is chosen (the parent row Id as text for the default key, or the parent's key-field
/// value for a Set-Key table — either way, exactly what's stored in the child's reference column),
/// up to 3 display values for the multi-column list, and a <see cref="Label"/> for the closed
/// input — always the parent's descriptive/picker field, so a Set-Key override (which puts the raw
/// key in <see cref="Value1"/>, e.g. a phone number) still reads the same way the default Record
/// ID# key does once a record is picked.</summary>
public class ReferenceOption
{
    public string Id { get; set; } = string.Empty;
    public string? Value1 { get; set; }
    public string? Value2 { get; set; }
    public string? Value3 { get; set; }
    public string? Label { get; set; }
}
