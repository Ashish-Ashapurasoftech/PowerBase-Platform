using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.ResolveDefaultReport;

public class ResolveDefaultReportQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IQueryContext _queryContext;

    public ResolveDefaultReportQueryHandler(
        IAppTableRepository tableRepo,
        IAppUserRepository appUserRepo,
        IReportRepository reportRepo,
        IQueryContext queryContext)
    {
        _tableRepo = tableRepo;
        _appUserRepo = appUserRepo;
        _reportRepo = reportRepo;
        _queryContext = queryContext;
    }

    public async Task<ReportDetailResult> HandleAsync(ResolveDefaultReportQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var everyoneDefault = await _reportRepo.GetDefaultByTableAsync(query.TablePublicId, ct)
            ?? throw new NotFoundException("DefaultReport", query.TablePublicId);

        var settings = ParseSettings(table.DefaultReportSettings);
        var selectedReport = everyoneDefault;

        if (settings.Mode == DefaultReportModes.RoleBased)
        {
            var rolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(table.AppId, _queryContext.UserId, ct);
            if (rolePublicId.HasValue
                && settings.RoleDefaults.TryGetValue(rolePublicId.Value.ToString(), out var mappedReportRaw)
                && Guid.TryParse(mappedReportRaw, out var mappedReportId)
                && await _reportRepo.BelongsToTableAsync(query.TablePublicId, mappedReportId, ct))
            {
                selectedReport = await _reportRepo.GetByPublicIdAsync(mappedReportId, ct);
            }
        }

        return MapToResult(selectedReport);
    }

    private static ReportDetailResult MapToResult(Report report)
    {
        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        return new ReportDetailResult
        {
            Id = report.PublicId,
            Name = report.Name,
            Description = report.Description,
            ReportType = report.ReportType,
            Visibility = report.Visibility,
            Definition = definition,
            IsDefault = report.IsDefault,
            DisplayOrder = report.DisplayOrder,
            ViewEditFormId = report.ViewEditFormPublicId,
            CreatedOn = report.CreatedOn,
        };
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

