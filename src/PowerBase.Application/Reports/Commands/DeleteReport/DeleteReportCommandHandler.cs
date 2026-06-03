using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.DeleteReport;

public class DeleteReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    public DeleteReportCommandHandler(IReportRepository reportRepo, IAppAccessService appAccessService, IAuditRepository auditRepo)
    {
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteReportCommand command, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetByPublicIdAsync(command.ReportPublicId, ct);
        if (report == null) throw new NotFoundException("Report", command.ReportPublicId);
        
        var appId = await _reportRepo.GetAppIdByPublicIdAsync(command.ReportPublicId, ct);

        var affected = await _reportRepo.DeleteAsync(command.ReportPublicId, ct);
        if (affected == 0)
            throw new NotFoundException("Report", command.ReportPublicId);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.Report, command.ReportPublicId.ToString(), $"Report deleted: {report.Name}", appId: appId, ct: ct);
    }
}
