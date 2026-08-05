namespace PowerBase.Domain.ValueObjects;

/// <summary>
/// Personal, per-user appearance overrides. Every field is nullable — null means
/// "inherit from the app's <see cref="AppBrandingSettings"/> default" rather than a
/// literal value, per the 2-tier precedence (user preference overrides branding default).
/// </summary>
public class UserPreferencesSettings
{
    public string? Theme { get; set; }
    public string? Accent { get; set; }
    public string? FontFamily { get; set; }
    public string? FontSize { get; set; }
    public string? Density { get; set; }
    public int? PageSize { get; set; }
    public string? BorderMode { get; set; }
}
