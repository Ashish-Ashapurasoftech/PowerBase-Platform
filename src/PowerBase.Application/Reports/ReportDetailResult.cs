namespace PowerBase.Application.Reports;

public class ReportDetailResult
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ReportType { get; init; } = "Table";
    public string Visibility { get; init; } = "Personal";
    public ReportDefinition Definition { get; init; } = new();
    public bool IsDefault { get; init; }
    public int DisplayOrder { get; init; }
    public Guid? ViewEditFormId { get; init; }
    /// <summary>Owning table, so callers listing an app's reports can group them by table
    /// without a follow-up request per table.</summary>
    public Guid TableId { get; init; }
    public string TableName { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
    public List<Guid> VisibleToRoleIds { get; init; } = [];
}
