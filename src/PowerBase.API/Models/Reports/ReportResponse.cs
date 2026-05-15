namespace PowerBase.API.Models.Reports;

public class ReportResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ReportType { get; init; } = "Table";
    public string Visibility { get; init; } = "Personal";
    public ReportDefinitionDto Definition { get; init; } = new();
    public bool IsDefault { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class ReportDefinitionDto
{
    public List<long> Columns { get; init; } = [];
    public long? SortFieldId { get; init; }
    public bool SortDesc { get; init; }
}
