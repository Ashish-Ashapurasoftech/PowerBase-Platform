namespace PowerBase.API.Models.Reports;

public class CreateReportRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public List<long> Columns { get; set; } = [];
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
}
