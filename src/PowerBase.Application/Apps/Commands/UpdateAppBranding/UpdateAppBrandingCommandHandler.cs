using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Apps.Commands.UpdateAppBranding;

public class UpdateAppBrandingCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateAppBrandingCommandHandler(IAppRepository appRepo, IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateAppBrandingCommand command, CancellationToken ct = default)
    {
        var validator = new UpdateAppBrandingCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Merge onto the existing stored settings rather than requiring the caller to
        // resend both blobs every time — Branding and Layout are edited from separate
        // tabs in App Settings (Branding vs Styles/Layout), so a request commonly only
        // carries one of the two.
        var existing = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);

        // Roles > Advanced Permissions > Branding & Layout: each part of the branding needs its own permission, and only
        // the parts that actually change are checked (a page that resends unchanged values is not refused for them).
        foreach (var code in ChangedParts(existing.Branding, existing.LayoutSettings, command))
            await _appAccessService.RequirePermissionByAppPublicIdAsync(command.AppPublicId, code, ct);

        var brandingStr = command.Branding is not null
            ? JsonSerializer.Serialize(command.Branding)
            : existing.Branding;
        var layoutStr = command.Layout is not null
            ? JsonSerializer.Serialize(command.Layout)
            : existing.LayoutSettings;

        var affected = await _appRepo.UpdateBrandingAsync(command.AppPublicId, brandingStr, layoutStr, ct);
        if (affected == 0)
            throw new NotFoundException("App", command.AppPublicId);
    }

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The Branding permission codes for the parts of the request that differ from what is stored.</summary>
    private static IEnumerable<string> ChangedParts(string? storedBranding, string? storedLayout, UpdateAppBrandingCommand command)
    {
        var codes = new HashSet<string>();
        if (command.Branding is { } b)
        {
            var old = Parse<AppBrandingSettings>(storedBranding);
            if (b.Theme != old.Theme || b.Accent != old.Accent || b.BrandColorHex != old.BrandColorHex)
                codes.Add(PermissionCodes.BrandingColor);
            if (b.FontFamily != old.FontFamily || b.FontSize != old.FontSize)
                codes.Add(PermissionCodes.BrandingFont);
            if (b.Density != old.Density || b.PageSize != old.PageSize || b.BorderMode != old.BorderMode)
                codes.Add(PermissionCodes.BrandingGrid);
        }
        if (command.Layout is { } l)
        {
            var old = Parse<AppLayoutSettings>(storedLayout);
            if (l.SidebarStyle != old.SidebarStyle || l.NavPosition != old.NavPosition || l.ContentWidth != old.ContentWidth
                || l.PanelStyle != old.PanelStyle || l.HeaderBehavior != old.HeaderBehavior)
                codes.Add(PermissionCodes.BrandingLayout);
        }
        return codes;
    }

    private static T Parse<T>(string? json) where T : new()
    {
        if (string.IsNullOrEmpty(json)) return new T();
        try { return JsonSerializer.Deserialize<T>(json, ReadOptions) ?? new T(); }
        catch (JsonException) { return new T(); }
    }
}
