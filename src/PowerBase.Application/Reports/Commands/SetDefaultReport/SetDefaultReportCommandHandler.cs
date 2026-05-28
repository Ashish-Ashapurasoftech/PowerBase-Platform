using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.SetDefaultReport;

public class SetDefaultReportCommandHandler
{
    private readonly IReportRepository _reportRepo;

    public SetDefaultReportCommandHandler(IReportRepository reportRepo)
    {
        _reportRepo = reportRepo;
    }

    public async Task HandleAsync(SetDefaultReportCommand command, CancellationToken ct = default)
    {
        await _reportRepo.SetDefaultAsync(command.TablePublicId, command.ReportPublicId, ct);
    }
}
