using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms;

namespace PowerBase.Application.Reports.Queries.GetReportGridEditRules;

public record GetReportGridEditRulesQuery(Guid TableId, Guid ReportId);

public record ReportGridEditRulesResult(
    IReadOnlyList<FormRuleGridEditCandidate> Candidates,
    IReadOnlyList<Guid> SelectedRuleIds);

/// <summary>Backs the "Grid Edit &amp; Form Rules" picker in a report's settings — every eligible
/// rule on the table (see FormRuleGridEditCandidate) alongside this specific report's currently
/// selected, ordered subset.</summary>
public class GetReportGridEditRulesQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IReportRepository _reportRepo;

    public GetReportGridEditRulesQueryHandler(IAppTableRepository tableRepo, IFormRuleRepository ruleRepo, IReportRepository reportRepo)
    {
        _tableRepo = tableRepo;
        _ruleRepo = ruleRepo;
        _reportRepo = reportRepo;
    }

    public async Task<ReportGridEditRulesResult> HandleAsync(GetReportGridEditRulesQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TableId, ct);
        var candidates = await _ruleRepo.ListGridEditCandidatesByTableIdAsync(table.Id, ct);
        var selected = await _reportRepo.GetGridEditRuleIdsAsync(query.ReportId, ct);
        return new ReportGridEditRulesResult(candidates, selected);
    }
}
