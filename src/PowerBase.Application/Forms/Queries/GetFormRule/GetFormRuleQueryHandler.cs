using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateFormRule;

namespace PowerBase.Application.Forms.Queries.GetFormRule;

public class GetFormRuleQueryHandler
{
    private readonly IFormRuleRepository _ruleRepo;

    public GetFormRuleQueryHandler(IFormRuleRepository ruleRepo) => _ruleRepo = ruleRepo;

    public async Task<FormRuleDetail> HandleAsync(GetFormRuleQuery query, CancellationToken ct = default)
    {
        var rule = await _ruleRepo.GetByPublicIdAsync(query.RulePublicId, ct);
        return CreateFormRuleCommandHandler.MapToDetail(rule);
    }
}
