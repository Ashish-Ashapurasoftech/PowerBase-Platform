using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.SetDefaultReport;

public class SetDefaultReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;

    public SetDefaultReportCommandHandler(IReportRepository reportRepo, IAppAccessService appAccessService)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(SetDefaultReportCommand command, CancellationToken ct = default)
    {
        await _appAccessService.RequireByReportPublicIdAsync(command.ReportPublicId, AppAccess.Admin, ct);
        await _reportRepo.SetDefaultAsync(command.TablePublicId, command.ReportPublicId, ct);
    }
}
