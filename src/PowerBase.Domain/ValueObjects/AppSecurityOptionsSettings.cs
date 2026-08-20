namespace PowerBase.Domain.ValueObjects;

public class AppSecurityOptionsSettings
{
    public bool AllowNonAdminsToCopy { get; set; } = false;
    public bool AllowNonAdminsToExport { get; set; } = true;
    public bool AllowNonAdminsToConnect { get; set; } = true;
    public bool HideFromPublicSearch { get; set; } = false;
    public bool AllowCrawlerIndexing { get; set; } = false;
    public bool RequireAppTokens { get; set; } = true;
    public bool OnlyApprovedUsersAccess { get; set; } = false;
    public bool OnlyApprovedIpAddressesAccess { get; set; } = false;
    public string? WrappedDek { get; set; }
}
