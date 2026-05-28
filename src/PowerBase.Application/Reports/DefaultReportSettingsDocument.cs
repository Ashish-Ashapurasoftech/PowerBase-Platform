using System.Text.Json.Serialization;

namespace PowerBase.Application.Reports;

public class DefaultReportSettingsDocument
{
    public string Mode { get; set; } = DefaultReportModes.Everyone;

    public Dictionary<string, string> RoleDefaults { get; set; } = [];
}

public static class DefaultReportModes
{
    public const string Everyone = "Everyone";
    public const string RoleBased = "RoleBased";

    public static bool IsValid(string mode) => mode is Everyone or RoleBased;
}

