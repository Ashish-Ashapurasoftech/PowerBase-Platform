using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports;

/// <summary>A form a report can select for Grid Edit, with how many APPLICABLE rules it has — active,
/// with at least one Require/PreventSave/Enable/Disable/ChangeValue/DisplayMessage action (the only
/// action types a client-side grid pre-check can act on).</summary>
public record GridEditFormOption(Guid Id, string Name, int RuleCount);

/// <summary>An applicable rule of a form (Id = the rule's PublicId).</summary>
public record GridEditRuleItem(Guid Id, string RuleName, Guid FormId, string FormName);

/// <summary>An applicable rule of a form THIS report has selected, with whether the user moved it to
/// the "Available" side (excluded). Returned in the report's own priority order.</summary>
public record GridEditRuleState(Guid Id, string RuleName, Guid FormId, string FormName, bool IsExcluded);

/// <summary>Everything the grid needs to enforce one applied rule, in one shot: the rule with its
/// conditions/actions, plus the element-id -> field-id map of its form (a rule action targets a form
/// ELEMENT; this saves the browser fetching each form's whole layout just to resolve that).</summary>
public class GridEditRuntimeRule
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid FormId { get; init; }
    public bool IsExpressionMode { get; init; }
    public string? ExpressionText { get; init; }
    public string ConditionLogic { get; init; } = "all";
    public List<FormRuleCondition> Conditions { get; init; } = [];
    public List<FormRuleAction> Actions { get; init; } = [];
    public Dictionary<long, long> ElementFieldMap { get; init; } = [];
}
