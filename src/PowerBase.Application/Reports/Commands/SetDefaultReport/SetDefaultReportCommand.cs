namespace PowerBase.Application.Reports.Commands.SetDefaultReport;

public record SetDefaultReportCommand(Guid TablePublicId, Guid ReportPublicId);
