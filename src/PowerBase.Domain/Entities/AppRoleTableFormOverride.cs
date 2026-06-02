namespace PowerBase.Domain.Entities;

public class AppRoleTableFormOverride
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long AppTableId { get; set; }
    public long? AppRoleId { get; set; }
    public long? EditFormId { get; set; }
    public long? AddFormId { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
}
