namespace PowerBase.Domain.Entities;

public class Form
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppTableId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool AutoAddNewFields { get; set; } = true;
    public bool ShowBuiltInFields { get; set; }
    public string SaveOptions { get; set; } = "SaveKeepWorking,SaveNew,SaveNext,SaveView";
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    // ── Grid-snap canvas (Phase 8) ──
    public string? PageNavMode { get; set; }
    public bool? AlwaysTabsOnView { get; set; }
    /// <summary>Per-form theme override, JSON-encoded (FormTheme on the frontend).
    /// Null = inherit the app's Branding tokens.</summary>
    public string? ThemeJson { get; set; }
}
