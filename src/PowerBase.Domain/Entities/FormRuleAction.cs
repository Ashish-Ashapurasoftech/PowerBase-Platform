namespace PowerBase.Domain.Entities;

public class FormRuleAction
{
    public long Id { get; set; }
    public long FormRuleId { get; set; }
    public string ActionType { get; set; } = "Show";
    public string TargetType { get; set; } = "Field";
    public long? TargetElementId { get; set; }
    public long? TargetSectionId { get; set; }
    public long? TargetBlockId { get; set; }
    public string? ActionValue { get; set; }
    /// <summary>ChangeValue only — "Run this action when the condition changes from false to
    /// true." See migration 059_formruleaction_add_run_once_on_activation.sql for the full
    /// rationale; enforced client-side (FormRendererComponent.evaluateRules), not here.</summary>
    public bool RunOnceOnActivation { get; set; }
    public int DisplayOrder { get; set; }
}
