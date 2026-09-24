namespace PowerBase.Application.Forms;

/// <summary>A Form Rule eligible for a report's Grid Edit rule picker — one that has at least one
/// Require/PreventSave/Enable/Disable/ChangeValue/DisplayMessage action, the only action types a
/// report's client-side Grid Edit pre-check can ever act on (see table-report-view's grid rule
/// evaluator). Show/Hide/ChangeLabel/SetColor/NotRequired-only rules never appear here — they have
/// no meaningful per-row grid equivalent.</summary>
public record FormRuleGridEditCandidate(Guid Id, string RuleName, string FormName, Guid FormId);
