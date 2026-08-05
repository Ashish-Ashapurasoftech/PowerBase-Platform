namespace PowerBase.Domain.ValueObjects;

/// <summary>
/// App-level appearance defaults (admin-managed, App Settings → Branding).
/// Personal <see cref="UserPreferencesSettings"/> override these per-user;
/// null fields there mean "inherit this value".
/// </summary>
public class AppBrandingSettings
{
    /// <summary>One of the 16 preset background themes (see ThemeName on the frontend).</summary>
    public string Theme { get; set; } = "light";

    /// <summary>One of the 10 preset accents, or "custom" when <see cref="BrandColorHex"/> is set.</summary>
    public string Accent { get; set; } = "emerald";

    public string FontFamily { get; set; } = "system";

    /// <summary>sm | md | lg</summary>
    public string FontSize { get; set; } = "md";

    /// <summary>compact | comfortable | spacious</summary>
    public string Density { get; set; } = "comfortable";

    public int PageSize { get; set; } = 10;

    /// <summary>horizontal | full-grid | borderless</summary>
    public string BorderMode { get; set; } = "horizontal";

    /// <summary>Optional custom tenant brand hex (e.g. "#0057ff"). When set, the frontend
    /// generates a 50-950 ramp from it at runtime and this takes priority over Accent.</summary>
    public string? BrandColorHex { get; set; }
}
