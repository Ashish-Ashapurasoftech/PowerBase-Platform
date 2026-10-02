using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Queries.GetReportGridEditRules;

public record GetReportGridEditRulesQuery(Guid TableId, Guid ReportId);

/// <param name="Forms">Every form on the table with its applicable-rule count.</param>
/// <param name="SelectedFormIds">The forms this report selected, in saved order.</param>
/// <param name="Applied">Rules of the selected forms that apply, in the report's priority order —
/// includes rules added to a selected form since the last save (they apply automatically).</param>
/// <param name="Available">Rules of the selected forms the user moved aside (excluded).</param>
public record ReportGridEditRulesResult(
    IReadOnlyList<GridEditFormOption> Forms,
    IReadOnlyList<Guid> SelectedFormIds,
    IReadOnlyList<GridEditRuleState> Applied,
    IReadOnlyList<GridEditRuleState> Available);

/// <summary>Backs the "Grid Edit &amp; Form Rules" picker. Deliberately does NOT return every rule on the
/// table — only the forms (with counts) and the rules of the forms this report has selected; a newly
/// selected form's rules are fetched on demand (GetGridEditFormRulesQueryHandler).</summary>
public class GetReportGridEditRulesQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IReportRepository _reportRepo;

    public GetReportGridEditRulesQueryHandler(IAppTableRepository tableRepo, IReportRepository reportRepo)
    {
        _tableRepo = tableRepo;
        _reportRepo = reportRepo;
    }

    public async Task<ReportGridEditRulesResult> HandleAsync(GetReportGridEditRulesQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TableId, ct);
        var forms = await _reportRepo.ListGridEditFormOptionsAsync(table.Id, ct);
        var selectedFormIds = await _reportRepo.GetGridEditFormIdsAsync(query.ReportId, ct);
        // Nothing selected -> nothing to load; skips the rules query entirely.
        var states = selectedFormIds.Count == 0
            ? Array.Empty<GridEditRuleState>()
            : await _reportRepo.ListGridEditRuleStatesAsync(query.ReportId, ct);
        return new ReportGridEditRulesResult(
            forms,
            selectedFormIds,
            states.Where(s => !s.IsExcluded).ToList(),
            states.Where(s => s.IsExcluded).ToList());
    }
}
