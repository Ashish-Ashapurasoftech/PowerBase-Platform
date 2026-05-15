using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.CreateReport;

public class CreateReportCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IQueryContext _queryContext;
    private readonly CreateReportCommandValidator _validator;

    public CreateReportCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IQueryContext queryContext)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _queryContext = queryContext;
        _validator = new CreateReportCommandValidator();
    }

    public async Task<ReportDetailResult> HandleAsync(CreateReportCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        if (command.Columns.Count > 0)
        {
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var validIds = fields.Select(f => f.Id).ToHashSet();
            var invalid = command.Columns.Where(id => !validIds.Contains(id)).ToList();
            if (invalid.Count > 0)
                throw new ValidationException(
                    new Dictionary<string, string[]> { ["columns"] = [$"Unknown field IDs: {string.Join(", ", invalid)}"] });
        }

        var definition = new ReportDefinition
        {
            Columns = command.Columns,
            SortFieldId = command.SortFieldId,
            SortDesc = command.SortDesc,
        };

        var report = new Report
        {
            TenantId = _queryContext.TenantId,
            AppTableId = table.Id,
            OwnerId = _queryContext.UserId,
            Name = command.Name,
            Description = command.Description,
            ReportType = "Table",
            Visibility = command.Visibility,
            Definition = JsonSerializer.Serialize(definition),
            IsDefault = false,
            DisplayOrder = 0,
        };

        var (_, publicId) = await _reportRepo.CreateAsync(report, ct);

        return new ReportDetailResult
        {
            Id = publicId,
            Name = report.Name,
            Description = report.Description,
            ReportType = report.ReportType,
            Visibility = report.Visibility,
            Definition = definition,
            IsDefault = report.IsDefault,
            DisplayOrder = report.DisplayOrder,
            CreatedOn = DateTime.UtcNow,
        };
    }
}
