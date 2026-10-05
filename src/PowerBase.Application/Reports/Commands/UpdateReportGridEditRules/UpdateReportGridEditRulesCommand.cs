using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Commands.UpdateReportGridEditRules;

/// <param name="FormIds">The forms the report selected.</param>
/// <param name="AppliedRuleIds">Rules that apply, in priority order (top wins on conflicts).</param>
/// <param name="ExcludedRuleIds">Rules of the selected forms the user moved aside.</param>
public record UpdateReportGridEditRulesCommand(
    Guid ReportId,
    IReadOnlyList<Guid> FormIds,
    IReadOnlyList<Guid> AppliedRuleIds,
    IReadOnlyList<Guid> ExcludedRuleIds);

/// <summary>Replaces a report's Grid Edit config wholesale (one transaction) — deliberately narrow,
/// not part of the monolithic UpdateReportCommand / the report's Definition JSON.</summary>
public class UpdateReportGridEditRulesCommandHandler
{
    private readonly IReportRepository _reportRepo;

    public UpdateReportGridEditRulesCommandHandler(IReportRepository reportRepo) => _reportRepo = reportRepo;

    public Task HandleAsync(UpdateReportGridEditRulesCommand command, CancellationToken ct = default)
        => _reportRepo.SetGridEditConfigAsync(
            command.ReportId,
            command.FormIds.Distinct().ToList(),
            command.AppliedRuleIds.Distinct().ToList(),
            // A rule can't be both applied and excluded — applied wins.
            command.ExcludedRuleIds.Distinct().Except(command.AppliedRuleIds).ToList(),
            ct);
}
