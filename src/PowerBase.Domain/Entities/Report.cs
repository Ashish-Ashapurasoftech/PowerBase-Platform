namespace PowerBase.Domain.Entities;

public class Report
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppTableId { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ReportType { get; set; } = "Table";
    public string Visibility { get; set; } = "Personal";
    public string Definition { get; set; } = "{}";
    public bool IsDefault { get; set; }
    /// <summary>Marks the one hidden, per-table row that backs "Default Report Settings" — never
    /// shown in any reports list, never selectable via "Set as default". Distinct from
    /// <see cref="IsDefault"/>, which is about which report opens when a viewer opens the table.
    /// See migration 055_add_report_default_settings_record.sql for why these had to be split.</summary>
    public bool IsDefaultSettingsRecord { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public long? ViewEditFormId { get; set; }
    public Guid? ViewEditFormPublicId { get; set; }
}
