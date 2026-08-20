namespace PowerBase.Domain.Entities;

public class AppField
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppTableId { get; set; }
    public int FieldTypeId { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? HelpText { get; set; }
    public string? Placeholder { get; set; }
    public string? Description { get; set; }
    public string? PhysicalColumnName { get; set; }
    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsRequired { get; set; }
    //public bool IsRequiredInForm { get; set; }
    public bool IsUnique { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsSystem { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsSortable { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsReportable { get; set; }
    public bool IsAuditable { get; set; } = true;
    public bool IsEncrypted { get; set; }
    public int? Fid { get; set; }
    public string? Settings { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
}
