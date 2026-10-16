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
    /// <summary>Stable identity of the target (see migration 077). Authoritative: the numeric ids above
    /// are re-derived from these. A GUID that matches no row means the target was deleted.</summary>
    public Guid? TargetElementPublicId { get; set; }
    public Guid? TargetSectionPublicId { get; set; }
    public Guid? TargetBlockPublicId { get; set; }
    /// <summary>TargetType "Page" — a form page (no numeric id exists for it on this table).</summary>
    public Guid? TargetPagePublicId { get; set; }
    public string? ActionValue { get; set; }
    /// <summary>ChangeValue only — "Run this action when the condition changes from false to
    /// true." See migration 059_formruleaction_add_run_once_on_activation.sql for the full
    /// rationale; enforced client-side (FormRendererComponent.evaluateRules), not here.</summary>
    public bool RunOnceOnActivation { get; set; }
    /// <summary>ChangeLabel/ChangeValue/DisplayMessage/PreventSave only — when true, ActionValue
    /// holds a formula expression (e.g. "You cannot save after " &amp; [Calculated Deadline])
    /// instead of a literal string, evaluated live against the record's current field values
    /// (including other live-computed Formula fields) the same way an Expression Mode rule
    /// condition is. See migration 060_formruleaction_add_is_expression_value.sql.</summary>
    public bool IsExpressionValue { get; set; }
    /// <summary>ChangeValue only: 'literal' | 'field' | 'clear' | 'formula' (see migration 078). Null on older
    /// actions = formula when <see cref="IsExpressionValue"/>, otherwise literal.</summary>
    public string? ValueSource { get; set; }
    /// <summary>ValueSource 'field': the Fid of the field whose value is copied into the target.</summary>
    public long? SourceFieldId { get; set; }
    /// <summary>DisplayMessage only: 'notification' | 'modal' | 'field' (see migration 079). Null = notification.
    /// 'field' shows the message with the field named by the TargetElement* columns.</summary>
    public string? MessageType { get; set; }
    /// <summary>DisplayMessage (notification / field): 'success' | 'info' | 'warning' | 'error' (migration 080). Null = info.</summary>
    public string? MessageSeverity { get; set; }
    public int DisplayOrder { get; set; }
}
