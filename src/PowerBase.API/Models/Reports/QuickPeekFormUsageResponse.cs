namespace PowerBase.API.Models.Reports;

/// <summary>A report that pins a specific Quick Peek form (Options.QuickPeekFormId).</summary>
public class QuickPeekFormUsageResponse
{
    public Guid ReportId { get; init; }
    public string ReportName { get; init; } = string.Empty;
    public Guid FormId { get; init; }
}
