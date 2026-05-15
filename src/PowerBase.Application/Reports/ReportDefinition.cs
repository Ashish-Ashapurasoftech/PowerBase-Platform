namespace PowerBase.Application.Reports;

public class ReportDefinition
{
    public List<long> Columns { get; set; } = [];
    public long? SortFieldId { get; set; }
    public bool SortDesc { get; set; }
}
