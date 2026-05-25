using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public class UpdateReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    private static readonly HashSet<string> AllowedOperators = ["eq", "ne", "contains", "startsWith", "gt", "gte", "lt", "lte"];
    private static readonly HashSet<string> AllowedFunctions = ["Count", "Sum", "Avg", "Min", "Max"];
    private static readonly HashSet<string> AllowedVisibilities = ["Personal", "Shared", "RoleScoped"];

    public UpdateReportCommandHandler(IReportRepository reportRepo, IAppAccessService appAccessService, IAuditRepository auditRepo)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateReportCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });
        if (!AllowedVisibilities.Contains(command.Visibility))
            throw new ValidationException(new Dictionary<string, string[]>
                { ["Visibility"] = [$"Visibility must be one of: {string.Join(", ", AllowedVisibilities)}"] });

        await _appAccessService.RequireByReportPublicIdAsync(command.ReportPublicId, AppAccess.Admin, ct);

        // Validate filters
        foreach (var filter in command.Filters)
        {
            if (!AllowedOperators.Contains(filter.Operator))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["filters"] = [$"Invalid operator '{filter.Operator}'. Allowed: {string.Join(", ", AllowedOperators)}"] });
        }

        // Validate aggregations
        foreach (var agg in command.Aggregations)
        {
            if (!AllowedFunctions.Contains(agg.Function))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["aggregations"] = [$"Invalid function '{agg.Function}'. Allowed: {string.Join(", ", AllowedFunctions)}"] });
        }

        var definition = new ReportDefinition
        {
            Columns = command.Columns,
            SortFieldId = command.SortFieldId,
            SortDesc = command.SortDesc,
            Filters = command.Filters.Select(f => new ReportFilter
            {
                FieldId = f.FieldId,
                Operator = f.Operator,
                Value = f.Value,
            }).ToList(),
            GroupByFieldId = command.GroupByFieldId,
            Aggregations = command.Aggregations.Select(a => new SummaryAggregation
            {
                FieldId = a.FieldId,
                Function = a.Function,
            }).ToList(),
        };

        var definitionJson = JsonSerializer.Serialize(definition);

        var affected = await _reportRepo.UpdateAsync(
            command.ReportPublicId, command.Name, command.Description,
            command.Visibility, definitionJson, ct);

        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Report, command.ReportPublicId.ToString(), ct: ct);
    }
}
