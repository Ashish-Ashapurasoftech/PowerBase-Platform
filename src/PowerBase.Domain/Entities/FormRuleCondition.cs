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
}
