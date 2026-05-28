namespace PowerBase.Domain.Entities;

public class FormRuleCondition
{
    public long Id { get; set; }
    public long FormRuleId { get; set; }
    public long AppFieldId { get; set; }
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
    public int DisplayOrder { get; set; }
}
