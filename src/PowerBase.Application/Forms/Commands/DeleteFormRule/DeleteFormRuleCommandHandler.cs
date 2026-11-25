using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Forms.Commands.DeleteFormRule;

public class DeleteFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IFieldReferenceIndexer _refIndexer;
    private readonly IFormRepository _formRepo;

    public DeleteFormRuleCommandHandler(
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IFormRepository formRepo,
        IFieldReferenceIndexer? refIndexer = null)
    {
        _formRepo = formRepo;
        _refIndexer = refIndexer ?? NullFieldReferenceIndexer.Instance;
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteFormRuleCommand command, CancellationToken ct = default)
    {
        var rule = await _ruleRepo.GetByPublicIdAsync(command.RulePublicId, ct);
        await _ruleRepo.DeleteAsync(command.RulePublicId, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.FormRule, rule.Id.ToString(), ct: ct,
            tableId: await _formRepo.GetTableIdByFormIdAsync(rule.FormId, ct));
        await _refIndexer.RemoveSourceAsync(FieldReferenceSourceTypes.FormRule, command.RulePublicId, ct);
    }
}
