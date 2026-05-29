using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Commands.UpdateReportFormOverrides;

public record ReportFormOverrideCommandDto(Guid ReportId, Guid? ViewEditFormId);

public record UpdateReportFormOverridesCommand(Guid TableId, List<ReportFormOverrideCommandDto> Overrides);

public class UpdateReportFormOverridesCommandHandler
{
    private readonly IReportRepository _reportRepo;

    public UpdateReportFormOverridesCommandHandler(IReportRepository reportRepo)
    {
        _reportRepo = reportRepo;
    }

    public async Task HandleAsync(UpdateReportFormOverridesCommand request, CancellationToken ct)
    {
        var overrides = request.Overrides.Select(o => (o.ReportId, o.ViewEditFormId));
        await _reportRepo.UpdateFormOverridesAsync(request.TableId, overrides, ct);
    }
}
