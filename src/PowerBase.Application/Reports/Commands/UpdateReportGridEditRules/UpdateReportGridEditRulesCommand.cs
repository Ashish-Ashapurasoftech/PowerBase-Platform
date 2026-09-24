using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Commands.UpdateReportGridEditRules;

public record UpdateReportGridEditRulesCommand(Guid ReportId, IReadOnlyList<Guid> OrderedFormRuleIds);

/// <summary>Replaces a report's Grid Edit rule selection wholesale — same delete-then-reinsert
/// shape as SetReportRolesAsync, deliberately narrow (doesn't touch the report's Definition JSON
/// or go through the monolithic UpdateReportCommand at all).</summary>
public class UpdateReportGridEditRulesCommandHandler
{
    private readonly IReportRepository _reportRepo;

    public UpdateReportGridEditRulesCommandHandler(IReportRepository reportRepo) => _reportRepo = reportRepo;

    public Task HandleAsync(UpdateReportGridEditRulesCommand command, CancellationToken ct = default)
        => _reportRepo.SetGridEditRulesAsync(command.ReportId, command.OrderedFormRuleIds, ct);
}
