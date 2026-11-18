namespace PowerBase.Application.FieldReferences;

/// <summary>What kind of thing holds a reference to a field. Stored in meta.FieldReference.SourceType.</summary>
public static class FieldReferenceSourceTypes
{
    public const string Report = "Report";
    public const string Form = "Form";
    public const string FormRule = "FormRule";
    public const string Field = "Field";
    /// <summary>A relationship (meta.Relationship), filed under its CHILD table — the one that owns the reference field.</summary>
    public const string Relationship = "Relationship";
    /// <summary>A dashboard page (meta.Page). One page can use fields of several tables.</summary>
    public const string Dashboard = "Dashboard";
    /// <summary>One role's Custom Rules (meta.AppRoleRecordFilter rows). The source id is the ROLE's id because
    /// saving the rules replaces every filter row of the role at once, so row ids never survive a save.</summary>
    public const string CustomRule = "CustomRule";
    /// <summary>A PowerFlow (meta.Pipeline). The source id is the pipeline's id; one pipeline can use fields of several
    /// tables, so each row is filed under the table of the field it uses and the Usage is <see cref="FieldReferenceUsages.PowerFlowStep"/> + the step's RefId.</summary>
    public const string PowerFlow = "PowerFlow";
    /// <summary>A saved import (meta.ImportDefinition). It can read one table and fill several, so each row is filed under the table of
    /// the field it names.</summary>
    public const string Import = "Import";

    public static readonly string[] All = [Report, Form, FormRule, Field, Relationship, Dashboard, CustomRule, PowerFlow, Import];
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
    /// <summary>A Reference field's dropdown filter reads this field.</summary>
    public const string ReferenceFilter = "reference-filter";

    // Relationship
    public const string RelationshipReference = "reference-field";
    public const string RelationshipKey = "key-field";
    public const string RelationshipDisplayKey = "display-key";
    public const string RelationshipDisplayLabel = "display-label";

    // Dashboard
    public const string DashboardFilter = "dashboard-filter";
    public const string DashboardSearch = "search-field";

    // Custom rule
    public const string ViewRule = "view-rule";
    public const string ModifyRule = "modify-rule";

    // PowerFlow: "step:" followed by the step's RefId (the Usage column holds 40 characters). The Usage tab shows the step's label.
    public const string PowerFlowStep = "step:";

    // Import (a filter or formula in an import reuses Filter / Formula)
    public const string WritesTo = "writes-to";
    public const string ReadsFrom = "reads-from";
    public const string MatchKey = "match-key";
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
    string Usage,
    /// <summary>For a Report: who owns it (null otherwise).</summary>
    long? OwnerId = null,
    /// <summary>For a Report: "Personal", "Shared", "MyRole", "SpecificRoles" or "Hidden" (null otherwise).</summary>
    string? Visibility = null);
