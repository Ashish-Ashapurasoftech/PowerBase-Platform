using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateFormRule;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Forms.Commands.DuplicateFormRule;

public class DuplicateFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public DuplicateFormRuleCommandHandler(
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<FormRuleDetail> HandleAsync(DuplicateFormRuleCommand command, CancellationToken ct = default)
    {
        var source = await _ruleRepo.GetByPublicIdAsync(command.RulePublicId, ct);
        var newName = command.Name ?? $"{source.Name} (copy)";

        var (_, newPublicId) = await _ruleRepo.DuplicateAsync(
            command.RulePublicId, newName, _queryContext.TenantId, _queryContext.UserId, ct);

        var created = await _ruleRepo.GetByPublicIdAsync(newPublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.FormRule, created.Id.ToString(), ct: ct);

        return CreateFormRuleCommandHandler.MapToDetail(created);
    }
}
