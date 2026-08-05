using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Models.Apps;

/// <summary>Mirrors the frontend's AppBranding TS interface exactly:
/// { appearance, layout } — both sides reuse the same settings shape for
/// storage and wire format, matching AppResponse/UpdateAppRequest's existing
/// convention of exposing AppFormattingSettings/AppSecurityOptionsSettings directly.</summary>
public class AppBrandingResponse
{
    public AppBrandingSettings Appearance { get; init; } = new();
    public AppLayoutSettings Layout { get; init; } = new();
}
