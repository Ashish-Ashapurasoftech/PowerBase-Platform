namespace PowerBase.Application.Reports;

public record DefaultReportSettingsResult(
    string Mode,
    Guid EveryoneReportId,
    IReadOnlyList<RoleDefaultResult> Roles,
    IReadOnlyList<DefaultReportListItem> Reports);

public record RoleDefaultResult(Guid RoleId, string RoleName, Guid? ReportId);

public record DefaultReportListItem(Guid Id, string Name, bool IsDefault);

