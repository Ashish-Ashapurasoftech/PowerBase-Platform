using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Forms.Commands.DeleteFormRule;

public class DeleteFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public DeleteFormRuleCommandHandler(
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteFormRuleCommand command, CancellationToken ct = default)
    {
        var rule = await _ruleRepo.GetByPublicIdAsync(command.RulePublicId, ct);
        await _ruleRepo.DeleteAsync(command.RulePublicId, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.FormRule, rule.Id.ToString(), ct: ct);
    }
}
