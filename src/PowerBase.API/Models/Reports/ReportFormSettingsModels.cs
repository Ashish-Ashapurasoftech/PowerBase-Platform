namespace PowerBase.API.Models.Reports;

public class ReportFormSettingsResponse
{
    public List<ReportFormOverrideResponse> Reports { get; init; } = [];
    public List<PowerBase.API.Models.Forms.FormListItemResponse> Forms { get; init; } = [];
}

public class ReportFormOverrideResponse
{
    public Guid ReportId { get; init; }
    public string ReportName { get; init; } = string.Empty;
    public Guid? FormId { get; init; }
}



public class UpdateReportFormSettingsRequest
{
    public List<ReportFormOverrideRequestItem> ReportOverrides { get; init; } = [];
}

public class ReportFormOverrideRequestItem
{
    public Guid ReportId { get; init; }
    public Guid? FormId { get; init; }
}
