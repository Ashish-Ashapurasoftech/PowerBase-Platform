namespace PowerBase.API.Models.Reports;

public class DefaultReportSettingsResponse
{
    public string Mode { get; init; } = "Everyone";
    public Guid EveryoneReportId { get; init; }
    public List<RoleDefaultResponse> Roles { get; init; } = [];
    public List<DefaultReportListItemResponse> Reports { get; init; } = [];
}

public class RoleDefaultResponse
{
    public Guid RoleId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public Guid? ReportId { get; init; }
}

public class DefaultReportListItemResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

public class UpdateDefaultReportSettingsRequest
{
    public string Mode { get; init; } = "Everyone";
    public Guid EveryoneReportId { get; init; }
    public Dictionary<Guid, Guid?> RoleDefaults { get; init; } = [];
}

