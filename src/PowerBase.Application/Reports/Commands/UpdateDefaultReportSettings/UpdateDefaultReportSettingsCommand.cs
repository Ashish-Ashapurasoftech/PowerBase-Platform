namespace PowerBase.Application.Reports.Commands.UpdateDefaultReportSettings;

public record UpdateDefaultReportSettingsCommand(
    Guid TablePublicId,
    string Mode,
    Guid EveryoneReportId,
    IReadOnlyDictionary<Guid, Guid?> RoleDefaults);

