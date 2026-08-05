using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Models.Apps;

/// <summary>Both fields are optional — Branding (appearance) and Layout are edited from
/// separate tabs in App Settings, so a request commonly carries only one of the two;
/// the handler merges onto whatever is already stored for the other.</summary>
public record UpdateAppBrandingRequest(AppBrandingSettings? Appearance, AppLayoutSettings? Layout);
