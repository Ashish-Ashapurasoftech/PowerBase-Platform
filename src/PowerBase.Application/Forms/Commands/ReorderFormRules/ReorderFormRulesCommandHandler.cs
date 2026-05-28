using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.ReorderFormRules;

public class ReorderFormRulesCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IFormRuleRepository _ruleRepo;

    public ReorderFormRulesCommandHandler(IFormRepository formRepo, IFormRuleRepository ruleRepo)
    {
        _formRepo = formRepo;
        _ruleRepo = ruleRepo;
    }

    public async Task HandleAsync(ReorderFormRulesCommand command, CancellationToken ct = default)
    {
        var form = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);
        await _ruleRepo.ReorderAsync(form.Id, command.OrderedRulePublicIds, ct);
    }
}
