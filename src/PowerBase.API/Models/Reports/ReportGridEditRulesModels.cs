namespace PowerBase.API.Models.Reports;

public class ReportGridEditRulesResponse
{
    public List<GridEditRuleCandidateResponse> Candidates { get; init; } = [];
    /// <summary>Ordered — the report's own configured priority (top of the picker's Selected list
    /// first).</summary>
    public List<Guid> SelectedRuleIds { get; init; } = [];
}

public class GridEditRuleCandidateResponse
{
    public Guid Id { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public string FormName { get; init; } = string.Empty;
    public Guid FormId { get; init; }
}

public class UpdateReportGridEditRulesRequest
{
    public List<Guid> OrderedFormRuleIds { get; init; } = [];
}
