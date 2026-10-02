namespace PowerBase.Domain.Entities;

public class FormRuleCondition
{
    public long Id { get; set; }
    public long FormRuleId { get; set; }
    /// <summary>'field' (default — compares AppFieldId's value) or 'role' (compares the
    /// evaluating user's role; AppFieldId is null in that case).</summary>
    public string ConditionKind { get; set; } = "field";
    public long? AppFieldId { get; set; }
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
    public string? ValueType { get; set; }
    public long? ValueFieldId { get; set; }
    public int DisplayOrder { get; set; }

    /// <summary>Optional refinement of 'changed'/'notChanged': also require the OLD value to match
    /// this operator/value (same vocabulary as a normal field condition — eq/ne/gt/lt/during/…).
    /// Null = old value unchecked. See migration 063.</summary>
    public string? ChangeFromOperator { get; set; }
    public string? ChangeFromValue { get; set; }
    public string? ChangeFromValueType { get; set; }
    public long? ChangeFromValueFieldId { get; set; }
    /// <summary>Same refinement, but against the NEW (current) value.</summary>
    public string? ChangeToOperator { get; set; }
    public string? ChangeToValue { get; set; }
    public string? ChangeToValueType { get; set; }
    public long? ChangeToValueFieldId { get; set; }
}
