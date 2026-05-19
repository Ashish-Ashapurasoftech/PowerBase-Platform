using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public class UpdateReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public UpdateReportCommandHandler(IReportRepository reportRepo, IAppFieldRepository fieldRepo)
    {
        _reportRepo = reportRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task HandleAsync(UpdateReportCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var allowed = new HashSet<string> { "Personal", "Shared", "RoleScoped" };
        if (!allowed.Contains(command.Visibility))
            throw new ValidationException(new Dictionary<string, string[]> { ["Visibility"] = [$"Visibility must be one of: {string.Join(", ", allowed)}."] });

        var report = await _reportRepo.GetByPublicIdAsync(command.PublicId, ct);

        if (command.Columns.Count > 0)
        {
            var fields = await _fieldRepo.ListByTableAsync(report.AppTableId, ct);
            var validIds = fields.Select(f => f.Id).ToHashSet();
            var invalid = command.Columns.Where(id => !validIds.Contains(id)).ToList();
            if (invalid.Count > 0)
                throw new ValidationException(
                    new Dictionary<string, string[]> { ["columns"] = [$"Unknown field IDs: {string.Join(", ", invalid)}"] });
        }

        var definition = new ReportDefinition
        {
            Columns = command.Columns.ToList(),
            SortFieldId = command.SortFieldId,
            SortDesc = command.SortDesc,
        };

        report.Name = command.Name;
        report.Description = command.Description;
        report.Visibility = command.Visibility;
        report.Definition = JsonSerializer.Serialize(definition);

        await _reportRepo.UpdateAsync(report, ct);
    }
}
