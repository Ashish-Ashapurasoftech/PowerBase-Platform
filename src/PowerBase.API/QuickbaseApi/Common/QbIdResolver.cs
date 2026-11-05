using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Every identifier the Quickbase-compatible API receives may be either our PublicId (a Guid) or our numeric Id —
/// Quickbase callers often send numbers (e.g. report "5"). A Guid is looked up by PublicId, a positive whole number by Id.
/// Lookups by number stay inside the caller's tenant database (and, for users, the caller's account), and every API still
/// runs its normal permission checks on the resolved record.
/// </summary>
public sealed class QbIdResolver
{
    private readonly IAppRepository _apps;
    private readonly IAppTableRepository _tables;
    private readonly IReportRepository _reports;
    private readonly IAppRoleRepository _roles;
    private readonly IUserRepository _users;
    private readonly ITenantRepository _tenants;
    private readonly IImportDefinitionRepository _imports;

    public QbIdResolver(IAppRepository apps, IAppTableRepository tables, IReportRepository reports, IAppRoleRepository roles,
        IUserRepository users, ITenantRepository tenants, IImportDefinitionRepository imports)
    {
        _apps = apps;
        _tables = tables;
        _reports = reports;
        _roles = roles;
        _users = users;
        _tenants = tenants;
        _imports = imports;
    }

    /// <summary>Kind of identifier a raw value is.</summary>
    public enum IdKind { Missing, PublicId, Number, Invalid }

    public static IdKind Classify(string? raw, out Guid publicId, out long number)
    {
        publicId = Guid.Empty;
        number = 0;
        if (string.IsNullOrWhiteSpace(raw)) return IdKind.Missing;
        var v = raw.Trim();
        if (Guid.TryParse(v, out publicId)) return IdKind.PublicId;
        if (long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) return IdKind.Number;
        return IdKind.Invalid;
    }

    private static Exception Bad(string raw, string param) => param is "to" or "from" or "tableId" or "appId" or "dbid"
        ? QbApiException.InvalidRequest(V1.Services.QbRecordService.APP_GONE)          // Quickbase's answer for an id it cannot use
        : QbApiException.BadRequest($"Invalid value '{raw}' for parameter '{param}': expected a Guid or a numeric id");

    /// <summary>Quickbase wording: "Required key 'x' not found" for body keys, "Required query 'x' not found" for URL parameters.</summary>
    private static Exception Required(string param) => QbApiException.BadRequest(param is "to" or "from" or "data" or "where"
        ? $"Required key '{param}' not found" : $"Required query '{param}' not found");

    public async Task<Guid> AppAsync(string? raw, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
                try { return await _apps.GetPublicIdByIdAsync(n, ct); }
                catch (NotFoundException) { throw QbApiException.InvalidRequest(V1.Services.QbRecordService.APP_GONE); }
            case IdKind.Missing: throw Required(param);
            default: throw Bad(raw!, param);
        }
    }

    public async Task<Guid> TableAsync(string? raw, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
                try { return (await _tables.GetByIdAsync(n, ct)).PublicId; }
                catch (NotFoundException) { throw QbApiException.InvalidRequest(V1.Services.QbRecordService.APP_GONE); }
            case IdKind.Missing: throw Required(param);
            default: throw Bad(raw!, param);
        }
    }

    /// <summary>A report id, looked up within the report's table (Quickbase report ids are table-scoped).</summary>
    public async Task<Guid> ReportAsync(string? raw, Guid tablePublicId, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
            {
                var table = await _tables.GetByPublicIdAsync(tablePublicId, ct);
                var report = (await _reports.ListAllByAppAsync(table.AppId, ct)).FirstOrDefault(r => r.Id == n && r.AppTableId == table.Id);
                // The app listing leaves out the table's "Default Report Settings" report; it is still a runnable report.
                if (report is null && await _reports.GetDefaultSettingsRecordAsync(tablePublicId, ct) is { } settings && settings.Id == n)
                    report = settings;
                return report?.PublicId ?? throw QbApiException.NotFound($"Report with Id \"{n}\" not found.");
            }
            case IdKind.Missing: throw Required(param);
            default: throw Bad(raw!, param);
        }
    }

    /// <summary>A role of the given app.</summary>
    public async Task<Guid?> RoleAsync(string? raw, long appId, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
                return (await _roles.ListDetailsByAppIdAsync(appId, ct)).FirstOrDefault(r => r.Id == n)?.PublicId
                    ?? throw QbApiException.NotFound($"Role {n} not found in this application");
            case IdKind.Missing: return null;
            default: throw Bad(raw!, param);
        }
    }

    /// <summary>A user of the caller's account (active or pending). Numeric ids of people outside the account are not found.</summary>
    public async Task<Guid> UserAsync(string? raw, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
            {
                Guid publicId;
                try { publicId = (await _users.GetByIdAsync(n, ct)).PublicId; }
                catch (NotFoundException) { throw QbApiException.NotFound($"User {n} not found"); }
                if (await _tenants.GetTenantUserByUserPublicIdAsync(publicId, ct) is null)
                    throw QbApiException.NotFound($"User {n} not found");
                return publicId;
            }
            case IdKind.Missing: throw Required(param);
            default: throw Bad(raw!, param);
        }
    }

    /// <summary>A saved import into the given table.</summary>
    public async Task<Guid> ImportAsync(string? raw, long destinationTableId, string param, CancellationToken ct)
    {
        switch (Classify(raw, out var g, out var n))
        {
            case IdKind.PublicId: return g;
            case IdKind.Number:
                return (await _imports.ListEntitiesByDestinationAsync(destinationTableId, ct)).FirstOrDefault(d => d.Id == n)?.PublicId
                    ?? throw QbApiException.NotFound($"Import {n} not found for this table");
            case IdKind.Missing: throw Required(param);
            default: throw Bad(raw!, param);
        }
    }
}
