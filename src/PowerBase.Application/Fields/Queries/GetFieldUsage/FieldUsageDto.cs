namespace PowerBase.Application.Fields.Queries.GetFieldUsage;

public record FieldUsageDto
{
    public List<FieldUsageFormItem> Forms { get; init; } = [];
    public List<FieldUsageReportItem> Reports { get; init; } = [];
    public List<FieldUsageRoleItem> Roles { get; init; } = [];
}

public record FieldUsageFormItem(Guid Id, string Name, bool IsExplicitlyPlaced);
public record FieldUsageReportItem(Guid Id, string Name, List<string> UsedAs);
public record FieldUsageRoleItem(Guid RoleId, string RoleName, string EffectiveAccess, bool IsCustom);
