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
    /// <summary>The newer kinds — relationships, dashboards, custom table rules — in one shape, so adding another
    /// kind later needs no new list here.</summary>
    public List<FieldUsageOtherItem> OtherItems { get; init; } = [];
}

/// <summary>A relationship, dashboard or custom table rule that uses the field.</summary>
/// <param name="Kind">"relationship", "dashboard" or "custom rule" (see <see cref="FieldUsageKinds"/>).</param>
/// <param name="Id">The item's public id; for a custom rule that is the role it belongs to.</param>
/// <param name="Owner">Display name of the owner when the kind has one (a dashboard); otherwise null.</param>
/// <param name="UsedAs">How it uses the field: "reference field", "key field", "filter", "search field", "view rule", ….</param>
/// <param name="TableId">The table the item is filed under: a relationship's child table, or the table the field is on.</param>
public record FieldUsageOtherItem(string Kind, Guid Id, string Name, string? Owner, List<string> UsedAs, Guid TableId, string TableName);

public static class FieldUsageKinds
{
    public const string Relationship = "relationship";
    public const string Dashboard = "dashboard";
    public const string CustomRule = "custom rule";
    /// <summary>A PowerFlow; its UsedAs lists the labels of the steps that use the field.</summary>
    public const string PowerFlow = "powerflow";
    /// <summary>A saved import; the table it is filed under is the table of the field.</summary>
    public const string Import = "import";
}

/// <summary>A form of the table. <paramref name="IsExplicitlyPlaced"/> is true when the form lays the field out itself.</summary>
public record FieldUsageFormItem(Guid Id, string Name, bool IsExplicitlyPlaced);

/// <param name="UsedAs">How the report uses it: "column", "filter", "sort", "group by", "aggregation", "dynamic filter", ….</param>
/// <param name="OwnerName">Display name of the report's owner (null when unknown).</param>
/// <param name="IsPersonal">True when only its owner can see the report (Visibility = Personal); every other report is a common one.</param>
public record FieldUsageReportItem(Guid Id, string Name, List<string> UsedAs, string? OwnerName = null, bool IsPersonal = false);

public record FieldUsageFormRuleItem(Guid Id, string Name, Guid FormId, string FormName, List<string> UsedAs);

public record FieldUsageFieldItem(Guid Id, string Name, Guid TableId, string TableName, List<string> UsedAs);

public record FieldUsageRoleItem(Guid RoleId, string RoleName, string EffectiveAccess, bool IsCustom);
