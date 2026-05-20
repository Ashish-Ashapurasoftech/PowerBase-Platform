namespace PowerBase.Application.Reports.Commands.UpdateReport;

public record UpdateReportCommand(Guid ReportPublicId, string Name, string? Description);
