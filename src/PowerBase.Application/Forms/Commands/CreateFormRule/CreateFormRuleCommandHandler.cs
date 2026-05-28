using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.CreateFormRule;

public class CreateFormRuleCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public CreateFormRuleCommandHandler(
        IFormRepository formRepo,
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<FormRuleDetail> HandleAsync(CreateFormRuleCommand command, CancellationToken ct = default)
    {
        var validator = new CreateFormRuleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var form = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);

        var rule = new FormRule
        {
            TenantId       = _queryContext.TenantId,
            FormId         = form.Id,
            Name           = command.Name,
            IsActive       = true,
            RunTrigger     = "AnyChange",
            ConditionLogic = "all",
            CreatedBy      = _queryContext.UserId,
        };

        var (_, publicId) = await _ruleRepo.CreateAsync(rule, ct);
        var created = await _ruleRepo.GetByPublicIdAsync(publicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.FormRule, created.Id.ToString(),
            ct: ct);

        return MapToDetail(created);
    }

    internal static FormRuleDetail MapToDetail(FormRule r) => new()
    {
        Id               = r.PublicId,
        Name             = r.Name,
        Description      = r.Description,
        Tags             = r.Tags,
        IsActive         = r.IsActive,
        IsExpressionMode = r.IsExpressionMode,
        ExpressionText   = r.ExpressionText,
        RunTrigger       = r.RunTrigger,
        ConditionLogic   = r.ConditionLogic,
        DisplayOrder     = r.DisplayOrder,
        CreatedOn        = r.CreatedOn,
        RowVersion       = r.RowVersion,
        Conditions       = r.Conditions.Select(c => new FormRuleConditionDetail
        {
            Id           = c.Id,
            AppFieldId   = c.AppFieldId,
            Operator     = c.Operator,
            Value        = c.Value,
            DisplayOrder = c.DisplayOrder,
        }).ToList(),
        Actions = r.Actions.Select(a => new FormRuleActionDetail
        {
            Id              = a.Id,
            ActionType      = a.ActionType,
            TargetType      = a.TargetType,
            TargetElementId = a.TargetElementId,
            TargetSectionId = a.TargetSectionId,
            DisplayOrder    = a.DisplayOrder,
        }).ToList(),
    };
}
