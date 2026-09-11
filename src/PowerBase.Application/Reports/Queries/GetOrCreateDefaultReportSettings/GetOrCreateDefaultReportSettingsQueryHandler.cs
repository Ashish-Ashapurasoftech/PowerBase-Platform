using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.GetOrCreateDefaultReportSettings;

/// <summary>Fetches the table's hidden Default Report Settings row (see
/// Report.IsDefaultSettingsRecord), creating it on first use if migration
/// 055_add_report_default_settings_record.sql hasn't backfilled it for this table yet — a table
/// created before this query existed, or before that migration ran, has none. Always returns the
/// same row on repeat calls.</summary>
public class GetOrCreateDefaultReportSettingsQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IQueryContext _queryContext;
    private readonly GetReportQueryHandler _getReportHandler;

    public GetOrCreateDefaultReportSettingsQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IQueryContext queryContext,
        GetReportQueryHandler getReportHandler)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _queryContext = queryContext;
        _getReportHandler = getReportHandler;
    }

    public async Task<ReportDetailResult> HandleAsync(GetOrCreateDefaultReportSettingsQuery query, CancellationToken ct = default)
    {
        var existing = await _reportRepo.GetDefaultSettingsRecordAsync(query.TablePublicId, ct);
        Guid publicId;
        if (existing is not null)
        {
            publicId = existing.PublicId;
        }
        else
        {
            var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
            var (_, newPublicId) = await _reportRepo.CreateAsync(new Report
            {
                AppTableId = table.Id,
                OwnerId = _queryContext.UserId,
                Name = "Default Report Settings",
                ReportType = "Table",
                Visibility = "Shared",
                Definition = JsonSerializer.Serialize(new ReportDefinition()),
                IsDefault = false,
                IsDefaultSettingsRecord = true,
                DisplayOrder = 0,
            }, ct);
            publicId = newPublicId;
        }

        return await _getReportHandler.HandleAsync(new GetReportQuery(publicId), ct);
    }
}
