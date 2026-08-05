using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Apps.Commands.UpdateAppBranding;

public record UpdateAppBrandingCommand(
    Guid AppPublicId,
    AppBrandingSettings? Branding,
    AppLayoutSettings? Layout
);
