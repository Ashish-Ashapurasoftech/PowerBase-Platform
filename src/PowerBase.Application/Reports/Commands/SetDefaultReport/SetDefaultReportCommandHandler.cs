using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.SetDefaultReport;

public class SetDefaultReportCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;

    public SetDefaultReportCommandHandler(IReportRepository reportRepo, IAppTableRepository tableRepo, IAuditRepository auditRepo)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SetDefaultReportCommand command, CancellationToken ct = default)
    {
        await _reportRepo.SetDefaultAsync(command.TablePublicId, command.ReportPublicId, ct);
        
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var report = await _reportRepo.GetByPublicIdAsync(command.ReportPublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Report, command.ReportPublicId.ToString(), $"Default report set: {report.Name} for table {table.Name}", appId: table.AppId, ct: ct);
    }
}
