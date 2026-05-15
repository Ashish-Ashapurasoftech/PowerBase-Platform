using PowerBase.API.Models.Records;

namespace PowerBase.API.Models.Reports;

public class ReportRunResponse
{
    public IReadOnlyList<ReportColumnDto> Columns { get; init; } = [];
    public IReadOnlyList<RecordResponse> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ReportColumnDto
{
    public long FieldId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
}
