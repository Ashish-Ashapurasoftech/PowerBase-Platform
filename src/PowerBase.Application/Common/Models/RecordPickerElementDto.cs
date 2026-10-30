namespace PowerBase.Application.Common.Models;

/// <summary>A form element's record picker choice with the table it lives on and the field it shows, so the picker
/// endpoint can check the element really belongs to the relationship it is asked about.</summary>
public class RecordPickerElementDto
{
    public long AppTableId { get; init; }
    /// <summary>Fid of the element's field (<c>FormElement.AppFieldId</c>); null for a non-field element.</summary>
    public long? AppFieldFid { get; init; }
    public string? Mode { get; init; }
    public Guid? ReportPublicId { get; init; }
    public long? FieldId { get; init; }
}
