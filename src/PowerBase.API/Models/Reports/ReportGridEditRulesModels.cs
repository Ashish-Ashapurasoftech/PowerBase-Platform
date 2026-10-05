using PowerBase.API.Models.Forms;

namespace PowerBase.API.Models.Reports;

public class ReportGridEditRulesResponse
{
    /// <summary>Every form on the table with its applicable-rule count.</summary>
    public List<GridEditFormOptionResponse> Forms { get; init; } = [];
    /// <summary>The forms this report selected, in saved order.</summary>
    public List<Guid> SelectedFormIds { get; init; } = [];
    /// <summary>Rules of the selected forms that apply, in the report's priority order.</summary>
    public List<GridEditRuleResponse> Applied { get; init; } = [];
    /// <summary>Rules of the selected forms the user moved aside.</summary>
    public List<GridEditRuleResponse> Available { get; init; } = [];
}

public class GridEditFormOptionResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int RuleCount { get; init; }
}

public class GridEditRuleResponse
{
    public Guid Id { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public Guid FormId { get; init; }
    public string FormName { get; init; } = string.Empty;
}

public class UpdateReportGridEditRulesRequest
{
    public List<Guid> FormIds { get; init; } = [];
    /// <summary>Priority order — top wins on conflicts.</summary>
    public List<Guid> AppliedRuleIds { get; init; } = [];
    public List<Guid> ExcludedRuleIds { get; init; } = [];
}

/// <summary>One applied rule with everything the grid needs to enforce it, so it makes a single request
/// on entering Grid Edit.</summary>
public class GridEditRuntimeRuleResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid FormId { get; init; }
    public bool IsExpressionMode { get; init; }
    public string? ExpressionText { get; init; }
    public string ConditionLogic { get; init; } = "all";
    public List<FormRuleConditionResponse> Conditions { get; init; } = [];
    public List<FormRuleActionResponse> Actions { get; init; } = [];
    /// <summary>Form element id -> field id: a rule action targets a form ELEMENT, so this replaces
    /// fetching the form's whole layout just to resolve it.</summary>
    public Dictionary<long, long> ElementFieldMap { get; init; } = [];
}
