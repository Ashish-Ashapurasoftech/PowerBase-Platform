using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.DeleteReport;

public class DeleteReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;

    public DeleteReportCommandHandler(IReportRepository reportRepo, IAppAccessService appAccessService)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(DeleteReportCommand command, CancellationToken ct = default)
    {
        await _appAccessService.RequireByReportPublicIdAsync(command.ReportPublicId, AppAccess.Admin, ct);

        var affected = await _reportRepo.DeleteAsync(command.ReportPublicId, ct);
        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);
    }
}
