namespace PowerBase.Domain.Entities;

/// <summary>One immutable snapshot of a field's settings — created on every settings-changing
/// Update or Restore (see PowerBase.Application.Fields.Versioning.FieldVersionService). Never
/// updated or deleted once written; a Restore appends a new row rather than touching the one it
/// restores from.</summary>
public class AppFieldVersion
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppFieldId { get; set; }
    public int Version { get; set; }
    public FieldVersionChangeType ChangeType { get; set; }
    public int? RestoredFromVersion { get; set; }
    public string CommitMessage { get; set; } = string.Empty;
    /// <summary>Platform-level user id (IQueryContext.UserId) — not an FK. core.[User] is a
    /// control-plane table and doesn't exist in a tenant database, so ChangedByName below is
    /// captured (denormalized) at write time instead of joined at read time — same pattern
    /// meta.AppUser already uses for UserName/UserEmail.</summary>
    public long ChangedByUserId { get; set; }
    public string ChangedByName { get; set; } = string.Empty;
    public DateTime ChangedOn { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
}

public enum FieldVersionChangeType
{
    Update = 1,
    Restore = 2,
}
