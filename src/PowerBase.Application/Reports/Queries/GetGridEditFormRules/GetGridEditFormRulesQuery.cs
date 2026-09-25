using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Reports.Queries.GetGridEditFormRules;

public record GetGridEditFormRulesQuery(Guid TableId, Guid FormId);

/// <summary>The applicable rules of ONE form — fetched when the user selects that form in the Grid Edit
/// picker, so a form's rules are only ever loaded once it is actually chosen.</summary>
public class GetGridEditFormRulesQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IReportRepository _reportRepo;

    public GetGridEditFormRulesQueryHandler(IAppTableRepository tableRepo, IReportRepository reportRepo)
    {
        _tableRepo = tableRepo;
        _reportRepo = reportRepo;
    }

    public async Task<IReadOnlyList<GridEditRuleItem>> HandleAsync(GetGridEditFormRulesQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TableId, ct);
        return await _reportRepo.ListGridEditRulesForFormAsync(table.Id, query.FormId, ct);
    }
}
