namespace PowerBase.Application.Relationships;

/// <summary>One selectable parent record for a Reference field picker: the value to submit when
/// this option is chosen (the parent row Id as text — the picker always submits it), up to 3
/// display values for the multi-column list, and a <see cref="Label"/> for the closed input. The
/// columns are only what the parent's record picker (or the form element's own choice) lists, as in
/// Quickbase; a Set-Key table's key value is not a column but rides along in <see cref="KeyValue"/>.</summary>
public class ReferenceOption
{
    public string Id { get; set; } = string.Empty;
    /// <summary>The same row's PublicId — never what's submitted/stored (that's always
    /// <see cref="Id"/>), but the row-fetch key a picker-driven consumer needs: it lets the
    /// picker's own "the user just selected this exact row" moment resolve straight to
    /// GET /tables/{tableId}/records/{id:guid} instead of translating <see cref="Id"/> (an
    /// internal value, and Alternate-Key-dependent for what it even represents) back into a GUID
    /// some other way.</summary>
    public Guid PublicId { get; set; }
    public string? Value1 { get; set; }
    public string? Value2 { get; set; }
    public string? Value3 { get; set; }
    public string? Label { get; set; }
    /// <summary>The parent's key-field value as text, only when the parent has a Set Key (or the relationship a Display
    /// Key): a record read back from the API shows that text in the Reference field instead of the row Id, so the picker
    /// uses it to find the option an existing value stands for. Never shown as a column. Null for a Record ID# key.</summary>
    public string? KeyValue { get; set; }
}
