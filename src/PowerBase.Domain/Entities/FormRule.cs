namespace PowerBase.Domain.Entities;

public class FormRule
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long FormId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Tags { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsExpressionMode { get; set; }
    public string? ExpressionText { get; set; }
    public string RunTrigger { get; set; } = "AnyChange";
    public string ConditionLogic { get; set; } = "all";
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<FormRuleCondition> Conditions { get; set; } = new();
    public List<FormRuleAction> Actions { get; set; } = new();
}
