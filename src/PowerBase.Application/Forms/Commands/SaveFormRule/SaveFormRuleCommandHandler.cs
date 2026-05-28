using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.SaveFormRule;

public class SaveFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public SaveFormRuleCommandHandler(
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SaveFormRuleCommand command, CancellationToken ct = default)
    {
        var validator = new SaveFormRuleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var rule = await _ruleRepo.GetByPublicIdAsync(command.RulePublicId, ct);

        var conditions = command.Conditions.Select(c => new FormRuleCondition
        {
            AppFieldId   = c.AppFieldId,
            Operator     = c.Operator,
            Value        = c.Value,
            DisplayOrder = c.DisplayOrder,
        }).ToList();

        var actions = command.Actions.Select(a => new FormRuleAction
        {
            ActionType      = a.ActionType,
            TargetType      = a.TargetType,
            TargetElementId = a.TargetElementId,
            TargetSectionId = a.TargetSectionId,
            DisplayOrder    = a.DisplayOrder,
        }).ToList();

        await _ruleRepo.SaveRuleBodyAsync(
            command.RulePublicId,
            command.Name,
            command.Description,
            command.Tags,
            command.IsActive,
            command.RunTrigger,
            command.ConditionLogic,
            command.IsExpressionMode,
            command.ExpressionText,
            conditions,
            actions,
            command.RowVersion,
            ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.FormRule, rule.Id.ToString(),
            ct: ct);
    }
}
