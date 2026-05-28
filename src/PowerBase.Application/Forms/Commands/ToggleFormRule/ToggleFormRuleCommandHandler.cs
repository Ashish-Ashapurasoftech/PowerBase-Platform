using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.ToggleFormRule;

public class ToggleFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;

    public ToggleFormRuleCommandHandler(IFormRuleRepository ruleRepo) => _ruleRepo = ruleRepo;

    public async Task HandleAsync(ToggleFormRuleCommand command, CancellationToken ct = default)
    {
        await _ruleRepo.GetByPublicIdAsync(command.RulePublicId, ct);
        await _ruleRepo.SetActiveAsync(command.RulePublicId, command.IsActive, ct);
    }
}
