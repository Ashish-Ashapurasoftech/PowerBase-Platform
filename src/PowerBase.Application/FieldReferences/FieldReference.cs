namespace PowerBase.Application.FieldReferences;

/// <summary>What kind of thing holds a reference to a field. Stored in meta.FieldReference.SourceType.</summary>
public static class FieldReferenceSourceTypes
{
    public const string Report = "Report";
    public const string Form = "Form";
    public const string FormRule = "FormRule";
    public const string Field = "Field";

    public static readonly string[] All = [Report, Form, FormRule, Field];
}

/// <summary>How a source uses the field. Stored in meta.FieldReference.Usage.</summary>
public static class FieldReferenceUsages
{
    // Report
    public const string Column = "column";
    public const string Filter = "filter";
    public const string FilterValue = "filter-value";
    public const string Sort = "sort";
    public const string GroupBy = "group-by";
    public const string Aggregation = "aggregation";
    public const string DynamicFilter = "dynamic-filter";
    public const string Chart = "chart";

    // Form
    public const string FormElement = "form-element";

    // Form rule
    public const string RuleCondition = "rule-condition";
    public const string RuleConditionValue = "rule-condition-value";
    public const string RuleExpression = "rule-expression";
    public const string RuleTarget = "rule-target";
    public const string RuleActionFormula = "rule-action-formula";

    // Field
    public const string Formula = "formula";
    public const string Lookup = "lookup";
    public const string Summary = "summary";
    public const string ReportLink = "report-link";
    public const string ActionButton = "action-button";
}

/// <summary>One row of meta.FieldReference: <see cref="SourceType"/>/<see cref="SourceId"/> uses
/// <see cref="TargetFieldId"/> in the way <see cref="Usage"/> describes. <see cref="SourceTableId"/>
/// is the table the source lives on (the target may be on another table — Lookup/Summary).</summary>
public sealed record FieldReferenceRow(
    string SourceType, long SourceId, long SourceTableId, long TargetFieldId, string Usage);

/// <summary>A reference joined with the display name/ids the Usage tab needs.</summary>
public sealed record FieldReferenceItem(
    string SourceType,
    long SourceId,
    Guid SourcePublicId,
    string SourceName,
    long SourceTableId,
    Guid SourceTablePublicId,
    string SourceTableName,
    /// <summary>For a FormRule: the form it belongs to (null otherwise).</summary>
    Guid? ParentPublicId,
    string? ParentName,
    string Usage);
