namespace PowerBase.Domain.Entities;

public class AppRoleRecordFilter
{
    public long Id { get; set; }
    public long AppRoleId { get; set; }
    public long AppTableId { get; set; }
    public string Conjunction { get; set; } = "AND";
    public string FilterJson { get; set; } = "[]";
    /// <summary>"View" (which records the role sees) or "Modify" (which records it may edit / delete).</summary>
    public string Kind { get; set; } = "View";

    /// <summary>A copy the caller may change: the metadata cache hands the same stored AppRoleRecordFilter to every request.</summary>
    public AppRoleRecordFilter Clone() => (AppRoleRecordFilter)MemberwiseClone();
}
