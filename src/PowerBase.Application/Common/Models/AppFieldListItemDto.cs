namespace PowerBase.Application.Common.Models;

/// <summary>Slim projection of <see cref="PowerBase.Domain.Entities.AppField"/> for the fields
/// grid — excludes PhysicalColumnName/DefaultValue/Settings and other columns not shown there.</summary>
public class AppFieldListItemDto
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Description { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsSortable { get; set; }
    public bool IsFilterable { get; set; }
    public bool IsReportable { get; set; }
    public bool IsAuditable { get; set; }
    public bool IsUnique { get; set; }
    public bool IsSystem { get; set; }
    public int? Fid { get; set; }
    public DateTime CreatedOn { get; set; }
}
