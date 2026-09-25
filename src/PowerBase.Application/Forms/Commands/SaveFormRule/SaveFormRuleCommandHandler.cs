using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Forms.Commands.SaveFormRule;

public class SaveFormRuleCommandHandler
{
    private readonly IFormRuleRepository _ruleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFormulaExpressionValidator _exprValidator;

    public SaveFormRuleCommandHandler(
        IFormRuleRepository ruleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IFormRepository formRepo,
        IAppFieldRepository fieldRepo,
        IFormulaExpressionValidator exprValidator)
    {
        _ruleRepo = ruleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _formRepo = formRepo;
        _fieldRepo = fieldRepo;
        _exprValidator = exprValidator;
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

        // Reject expression-mode rules whose condition expression doesn't compile
        // against the table's fields (it must return a boolean).
        if (command.IsExpressionMode && !string.IsNullOrWhiteSpace(command.ExpressionText))
        {
            var tableId = await _formRepo.GetTableIdByFormIdAsync(rule.FormId, ct);
            if (tableId is { } tid)
            {
                var tableFields = await _fieldRepo.ListByTableAsync(tid, ct);
                var errors = _exprValidator.Validate(command.ExpressionText, tableFields, FormulaType.Bool);
                if (errors.Count > 0)
                    throw new ValidationException(new Dictionary<string, string[]> { ["ExpressionText"] = errors.ToArray() });
            }
        }

        // Same compile-check for any action whose ActionValue is a formula expression
        // (ChangeLabel/ChangeValue/DisplayMessage/PreventSave, gated by IsExpressionValue).
        // ChangeLabel/DisplayMessage/PreventSave stay unconstrained (expectedType null) — whatever
        // they produce is turned into text before use. ChangeValue is different: its result is
        // written straight into the TARGET field's control, so the formula must produce that
        // field's own type (a Text result into a Rating field used to validate fine and misbehave
        // later) — resolved via the action's target form element -> AppField -> TypeCode.
        var expressionActionErrors = new List<string>();
        var expressionActions = command.Actions.Where(a => a.IsExpressionValue && !string.IsNullOrWhiteSpace(a.ActionValue)).ToList();
        if (expressionActions.Count > 0)
        {
            var tableId = await _formRepo.GetTableIdByFormIdAsync(rule.FormId, ct);
            if (tableId is { } tid)
            {
                var tableFields = await _fieldRepo.ListByTableAsync(tid, ct);
                Dictionary<long, long?>? elementFieldMap = null;
                foreach (var a in expressionActions)
                {
                    FormulaType? expected = null;
                    if (a.ActionType == "ChangeValue" && a.TargetElementId is { } elementId)
                    {
                        elementFieldMap ??= (await _formRepo.GetLayoutAsync(rule.FormId, ct))
                            .SelectMany(sec => sec.Blocks).SelectMany(b => b.Elements)
                            .ToDictionary(e => e.Id, e => e.AppFieldId);
                        if (elementFieldMap.TryGetValue(elementId, out var fid) && fid is { } fidVal)
                        {
                            var target = tableFields.FirstOrDefault(f => f.Fid.HasValue && (long)f.Fid.Value == fidVal);
                            expected = ExpectedTypeForTarget(target?.TypeCode);
                        }
                    }
                    var errors = _exprValidator.Validate(a.ActionValue, tableFields, expected);
                    expressionActionErrors.AddRange(errors.Select(e => $"{a.ActionType}: {e}"));
                }
            }
        }
        if (expressionActionErrors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["Actions"] = expressionActionErrors.ToArray() });

        var conditions = command.Conditions.Select(c => new FormRuleCondition
        {
            ConditionKind = c.ConditionKind,
            AppFieldId   = c.AppFieldId,
            Operator     = c.Operator,
            Value        = c.Value,
            ValueType    = c.ValueType,
            ValueFieldId = c.ValueFieldId,
            DisplayOrder = c.DisplayOrder,
        }).ToList();

        var actions = command.Actions.Select(a => new FormRuleAction
        {
            ActionType          = a.ActionType,
            TargetType          = a.TargetType,
            TargetElementId     = a.TargetElementId,
            TargetSectionId     = a.TargetSectionId,
            TargetBlockId       = a.TargetBlockId,
            ActionValue         = a.ActionValue,
            RunOnceOnActivation = a.RunOnceOnActivation,
            IsExpressionValue   = a.IsExpressionValue,
            DisplayOrder        = a.DisplayOrder,
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

    /// <summary>The formula type a Change value formula must produce to fit a target field of this
    /// TypeCode, or null when no single formula type matches (MultiSelect, File, Address, ranges,
    /// …) — those stay unconstrained. Mirrors the frontend's expectedTypeFor
    /// (rule-action-row.component.ts) so the live "Valid" badge and this save-time check agree.</summary>
    private static FormulaType? ExpectedTypeForTarget(string? typeCode) => typeCode switch
    {
        "Text" or "TextMultiLine" or "RichText" or "Email" or "Phone" or "Url" or "SingleSelect" => FormulaType.Text,
        "Number" or "Currency" or "Percent" or "Rating" => FormulaType.Number,
        "Date" => FormulaType.Date,
        "DateTime" => FormulaType.DateTime,
        "Time" => FormulaType.Time,
        "Duration" => FormulaType.Duration,
        "Boolean" => FormulaType.Bool,
        "User" => FormulaType.User,
        _ => null,
    };
}
