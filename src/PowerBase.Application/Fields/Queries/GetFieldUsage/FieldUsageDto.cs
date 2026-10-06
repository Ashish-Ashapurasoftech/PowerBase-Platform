namespace PowerBase.Application.Fields.Queries.GetFieldUsage;

/// <summary>Everything that uses one field. Forms, reports, form rules and other fields come from the
/// meta.FieldReference index; roles from the field's permission rows.</summary>
public record FieldUsageDto
{
    public List<FieldUsageFormItem> Forms { get; init; } = [];
    public List<FieldUsageReportItem> Reports { get; init; } = [];
    /// <summary>Form rules whose conditions, expression, targets or action formulas use the field.</summary>
    public List<FieldUsageFormRuleItem> FormRules { get; init; } = [];
    /// <summary>Other fields that read it: formulas, Lookups, Summaries, Report Links, Action Buttons.</summary>
    public List<FieldUsageFieldItem> Fields { get; init; } = [];
    public List<FieldUsageRoleItem> Roles { get; init; } = [];
}

/// <summary>A form of the table. <paramref name="IsExplicitlyPlaced"/> is true when the form lays the field out itself.</summary>
public record FieldUsageFormItem(Guid Id, string Name, bool IsExplicitlyPlaced);

/// <param name="UsedAs">How the report uses it: "column", "filter", "sort", "group by", "aggregation", "dynamic filter", ….</param>
public record FieldUsageReportItem(Guid Id, string Name, List<string> UsedAs);

public record FieldUsageFormRuleItem(Guid Id, string Name, Guid FormId, string FormName, List<string> UsedAs);

public record FieldUsageFieldItem(Guid Id, string Name, Guid TableId, string TableName, List<string> UsedAs);

public record FieldUsageRoleItem(Guid RoleId, string RoleName, string EffectiveAccess, bool IsCustom);
