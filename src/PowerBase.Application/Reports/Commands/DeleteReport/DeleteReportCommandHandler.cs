using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Commands.DeleteReport;

public class DeleteReportCommandHandler
{
    private readonly IReportRepository _reportRepo;

    public DeleteReportCommandHandler(IReportRepository reportRepo)
    {
        _reportRepo = reportRepo;
    }

    public async Task HandleAsync(DeleteReportCommand command, CancellationToken ct = default)
    {
        await _reportRepo.DeleteAsync(command.PublicId, ct);
    }
}
