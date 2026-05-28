using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateFormRule;

namespace PowerBase.Application.Forms.Queries.ListFormRules;

public class ListFormRulesQueryHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IFormRuleRepository _ruleRepo;

    public ListFormRulesQueryHandler(IFormRepository formRepo, IFormRuleRepository ruleRepo)
    {
        _formRepo = formRepo;
        _ruleRepo = ruleRepo;
    }

    public async Task<IReadOnlyList<FormRuleDetail>> HandleAsync(ListFormRulesQuery query, CancellationToken ct = default)
    {
        var form = await _formRepo.GetByPublicIdAsync(query.FormPublicId, ct);
        var rules = await _ruleRepo.ListByFormAsync(form.Id, ct);
        return rules.Select(CreateFormRuleCommandHandler.MapToDetail).ToList();
    }
}
