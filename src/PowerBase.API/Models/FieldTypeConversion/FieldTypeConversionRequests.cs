namespace PowerBase.API.Models.FieldTypeConversion;

public sealed class PreviewFieldTypeChangeRequest
{
    /// <summary>The field type code to change to, e.g. "Number".</summary>
    public string TargetTypeCode { get; set; } = string.Empty;
}

public sealed class ChangeFieldTypeRequest
{
    /// <summary>The field type code to change to, e.g. "Number".</summary>
    public string TargetTypeCode { get; set; } = string.Empty;

    /// <summary>Required reason for the change (recorded in the activity log).</summary>
    public string? CommitMessage { get; set; }
}
