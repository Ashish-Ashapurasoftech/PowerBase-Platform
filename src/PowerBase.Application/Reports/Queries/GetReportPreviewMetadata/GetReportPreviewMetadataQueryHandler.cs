using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.GetReportPreviewMetadata;

public class GetReportPreviewMetadataQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RunReportQueryHandler _runHandler;

    public GetReportPreviewMetadataQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RunReportQueryHandler runHandler)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _runHandler = runHandler;
    }

    public async Task<ReportPreviewMetadataDto> HandleAsync(GetReportPreviewMetadataQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetVisibleReportAsync(query.ReportPublicId, ct)
            ?? throw new NotFoundException("Report", query.ReportPublicId);

        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var fieldMap = allFields.ToDictionary(f => f.Fid.HasValue ? (long)f.Fid.Value : f.Id);

        var runResult = await _runHandler.HandleAsync(new RunReportQuery(query.ReportPublicId, Page: 1, PageSize: 1), ct);

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        var aggregations = new List<ReportAggregationPreviewDto>();

        foreach (var agg in definition.Aggregations)
        {
            var fieldLabel = fieldMap.TryGetValue(agg.FieldId, out var field)
                ? (string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)
                : $"Field #{agg.FieldId}";

            aggregations.Add(new ReportAggregationPreviewDto
            {
                FieldId = agg.FieldId,
                Function = agg.Function,
                DisplayAs = agg.DisplayAs,
                Label = $"{agg.Function} of {fieldLabel}"
            });
        }

        return new ReportPreviewMetadataDto
        {
            ReportId = report.PublicId,
            ReportName = report.Name,
            ReportType = report.ReportType,
            TableId = table.PublicId,
            TableName = table.Name,
            TotalCount = runResult.TotalCount,
            Columns = runResult.Columns,
            Aggregations = aggregations,
            IsDataMasked = true
        };
    }
}
