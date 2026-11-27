using System.Text.RegularExpressions;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Common;

/// <summary>
/// Per-table "View" builder permissions (View Tables / Fields / Reports / Forms — Roles > Advanced Permissions) guard an
/// item's DEFINITION, i.e. its detail page: a field's detail, a report in the builder, a form in the designer, a table's
/// Advanced settings. They are enforced only when BOTH hold:
/// <list type="number">
/// <item>the API call is that module's detail call (lists are never blocked by View), and</item>
/// <item>the app page making it is that module's detail page — read from the <see cref="HeaderName"/> header the
/// frontend sends on every call. Without the header (a direct API call / integration) only (1) applies.</item>
/// </list>
/// Pages that show data (report view, records, dashboards …) load the same definitions and are governed by Data
/// Permissions. The header comes from the browser: a convenience boundary for definitions only, never record data.
/// </summary>
public static partial class PageContext
{
    /// <summary>Must match PAGE_CONTEXT_HEADER in the frontend (core/api/interceptors/page-context.interceptor.ts).</summary>
    public const string HeaderName = "Pbcc";

    private const string Guid = "[0-9a-fA-F-]{36}";

    // ── API detail calls per module (request path, GET) ──
    [GeneratedRegex($@"^/tables/{Guid}/fields/{Guid}(/|$)")] private static partial Regex FieldDetailApi();
    [GeneratedRegex($@"^/reports/{Guid}/?$")] private static partial Regex ReportDetailApi();
    [GeneratedRegex($@"^/forms/({Guid}(/layout|/rules(/details)?)?|rules/{Guid})/?$")] private static partial Regex FormDetailApi();
    [GeneratedRegex($@"^/tables/{Guid}/advanced-settings/?$")] private static partial Regex TableDetailApi();

    // ── App detail pages per module (frontend route path) ──
    [GeneratedRegex($@"^/app/[^/]+/tables/[^/]+/settings/fields/{Guid}(/|$)")] private static partial Regex FieldDetailPage();
    [GeneratedRegex($@"^/app/[^/]+/tables/[^/]+/settings/reports/{Guid}/edit(/|$)")] private static partial Regex ReportDetailPage();
    [GeneratedRegex($@"^/app/[^/]+/tables/[^/]+/settings/forms/{Guid}(/|$)")] private static partial Regex FormDetailPage();
    [GeneratedRegex(@"^/app/[^/]+/tables/[^/]+/settings/advanced(/|$)")] private static partial Regex TableDetailPage();

    /// <summary>True when this View code must be checked per table for this request (see the class summary).</summary>
    public static bool EnforcesTableView(string permissionCode, string? apiPath, string? clientPage)
    {
        if (string.IsNullOrEmpty(apiPath)) return false;
        var (api, page) = permissionCode switch
        {
            PermissionCodes.FieldsRead => (FieldDetailApi(), FieldDetailPage()),
            PermissionCodes.ReportsRead => (ReportDetailApi(), ReportDetailPage()),
            PermissionCodes.FormsRead => (FormDetailApi(), FormDetailPage()),
            PermissionCodes.TablesRead => (TableDetailApi(), TableDetailPage()),
            _ => ((Regex?)null, (Regex?)null),
        };
        if (api is null || !api.IsMatch(apiPath)) return false;
        return string.IsNullOrWhiteSpace(clientPage) || page!.IsMatch(clientPage);
    }
}
