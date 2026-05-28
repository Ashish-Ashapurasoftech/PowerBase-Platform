using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.GetDefaultReportSettings;

public class GetDefaultReportSettingsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IReportRepository _reportRepo;

    public GetDefaultReportSettingsQueryHandler(
        IAppTableRepository tableRepo,
        IAppRoleRepository appRoleRepo,
        IReportRepository reportRepo)
    {
        _tableRepo = tableRepo;
        _appRoleRepo = appRoleRepo;
        _reportRepo = reportRepo;
    }

    public async Task<DefaultReportSettingsResult> HandleAsync(GetDefaultReportSettingsQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var reports = await _reportRepo.ListByTableAsync(query.TablePublicId, ct);
        var everyoneDefault = reports.FirstOrDefault(r => r.IsDefault)
            ?? throw new NotFoundException("DefaultReport", query.TablePublicId);

        var settings = ParseSettings(table.DefaultReportSettings);
        if (!DefaultReportModes.IsValid(settings.Mode))
            settings.Mode = DefaultReportModes.Everyone;

        var roles = await _appRoleRepo.ListDetailsByAppIdAsync(table.AppId, ct);
        var roleDefaults = settings.RoleDefaults
            .Where(kvp => Guid.TryParse(kvp.Key, out _) && Guid.TryParse(kvp.Value, out _))
            .ToDictionary(kvp => Guid.Parse(kvp.Key), kvp => Guid.Parse(kvp.Value));

        return new DefaultReportSettingsResult(
            settings.Mode,
            everyoneDefault.PublicId,
            roles.Select(r => new RoleDefaultResult(
                r.PublicId,
                r.Name,
                roleDefaults.TryGetValue(r.PublicId, out var reportId) ? reportId : null)).ToList(),
            reports.Select(r => new DefaultReportListItem(r.PublicId, r.Name, r.IsDefault)).ToList());
    }

    private static DefaultReportSettingsDocument ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new DefaultReportSettingsDocument();

        try
        {
            return JsonSerializer.Deserialize<DefaultReportSettingsDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new DefaultReportSettingsDocument();
        }
        catch (JsonException)
        {
            return new DefaultReportSettingsDocument();
        }
    }
}

