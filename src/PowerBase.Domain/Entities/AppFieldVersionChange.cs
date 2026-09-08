namespace PowerBase.Domain.Entities;

/// <summary>One changed property within an <see cref="AppFieldVersion"/> — structured (not a
/// free-text description) so the Audit History detail view can render a Setting / Previous / New
/// table. PropertyName is a dotted path for nested Settings JSON properties, e.g. "IsRequired" or
/// "Settings.MaxLength" (see PowerBase.Application.Fields.Versioning.FieldSnapshotDiffer).</summary>
public class AppFieldVersionChange
{
    public long Id { get; set; }
    public long AppFieldVersionId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
