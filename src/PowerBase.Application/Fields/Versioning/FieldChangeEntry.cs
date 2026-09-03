namespace PowerBase.Application.Fields.Versioning;

/// <summary>One changed property, ready to persist as an AppFieldVersionChange row or render as a
/// Setting / Previous / New table row. PropertyName is a dotted path for a nested Settings
/// property (e.g. "Settings.MaxLength") — see FieldSnapshotDiffer.</summary>
public record FieldChangeEntry(string PropertyName, string? OldValue, string? NewValue);
