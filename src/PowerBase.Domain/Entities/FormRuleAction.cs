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
    public int DisplayOrder { get; set; }
}
