using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReport;

public class UpdateReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateReportCommandHandler(IReportRepository reportRepo, IAppAccessService appAccessService)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateReportCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });

        await _appAccessService.RequireByReportPublicIdAsync(command.ReportPublicId, AppAccess.Admin, ct);

        var affected = await _reportRepo.UpdateAsync(command.ReportPublicId, command.Name, command.Description, ct);
        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);
    }
}
