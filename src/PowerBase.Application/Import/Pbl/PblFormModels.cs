namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// A form (QBL's <c>QB::FormV2</c> — the legacy <c>QB::Form</c> representation is intentionally
/// not imported; FormV2 is confirmed to be the one actually used for role-based view/edit/add
/// assignment). Cross-cutting per table, so lives as a top-level list on
/// <see cref="PblDocument"/>, matching <see cref="PblRelationship"/>.
///
/// Structure is <c>Form → Section → Block → Element</c>, matching PowerBase's own
/// <c>Form → FormSection → FormSectionBlock → FormElement</c> exactly — QBL's extra Page tier
/// (above Section) has no PowerBase equivalent and is dropped by the converter (Pages
/// contribute ordering only; their Sections are flattened in document order).
/// </summary>
public sealed class PblForm
{
    public string LogicalRef { get; set; } = string.Empty;

    /// <summary>A <see cref="PblTable.LogicalRef"/>.</summary>
    public string TableRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public List<PblFormSection> Sections { get; set; } = [];

    /// <summary>Form-level settings. Null = leave PowerBase's default.</summary>
    public bool? AutoAddNewFields { get; set; }
    public bool? ShowBuiltInFields { get; set; }
    public bool? AlwaysTabsOnView { get; set; }

    /// <summary>PowerBase's comma-separated save buttons (SaveKeepWorking,SaveNew,SaveNext,SaveView); null = default (all four).</summary>
    public string? SaveOptions { get; set; }

    /// <summary>"tabs" or "steps" — how a multi-page form's pages are navigated.</summary>
    public string? PageNavMode { get; set; }

    /// <summary>Pages, in order. Empty for a single-page form (PowerBase then has no page tier).</summary>
    public List<PblFormPage> Pages { get; set; } = [];

    /// <summary>Populated by the Form Rules slice; always empty until then.</summary>
    public List<PblFormRule> Rules { get; set; } = [];
}

public sealed class PblFormPage
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Heading { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public sealed class PblFormSection
{
    /// <summary>The <see cref="PblFormPage.LogicalRef"/> this section sits on; null on a single-page form.</summary>
    public string? PageRef { get; set; }

    /// <summary>A pinned section stays visible above every page.</summary>
    public bool IsPinned { get; set; }
    public bool? ShowDividers { get; set; }

    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; }

    /// <summary>Must have 1–5 blocks — matches <c>SaveFormLayoutCommandValidator</c>'s own limit.</summary>
    public List<PblFormBlock> Blocks { get; set; } = [];
}

public sealed class PblFormBlock
{
    public string LogicalRef { get; set; } = string.Empty;
    public string? Heading { get; set; }

    /// <summary>Hex color, e.g. "#RRGGBB" — must match SaveFormLayoutCommandValidator's regex when present.</summary>
    public string? BackgroundColor { get; set; }
    public int? Width { get; set; }

    public List<PblFormElement> Elements { get; set; } = [];
}

/// <summary>A dynamic form rule. QBL always wraps a rule's conditions in exactly one top-level
/// <c>Condition::Group</c> (confirmed real shape) — that group's own <c>TrueWhen</c> becomes
/// this rule's single <see cref="ConditionLogic"/>, since PowerBase has no per-nested-group
/// logic (a QBL rule with genuinely mixed nested AND/OR groups has no PowerBase equivalent and
/// is flagged, not flattened).</summary>
public sealed class PblFormRule
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>One of PowerBase's FormRunTrigger: AnyChange, EditOrAdd, Save, SaveAfterValidating.</summary>
    public string RunTrigger { get; set; } = "AnyChange";

    /// <summary>"all" or "any" — matches SaveFormRuleCommandValidator's lowercase values exactly.</summary>
    public string ConditionLogic { get; set; } = "all";

    /// <summary>When true, <see cref="Conditions"/> is ignored and <see cref="ExpressionText"/>
    /// (already translated PowerBase-syntax formula) drives the rule instead — the real mapping
    /// target for QBL's "Formula is true" condition, not an approximation.</summary>
    public bool IsExpressionMode { get; set; }
    public string? ExpressionText { get; set; }

    public List<PblFormRuleCondition> Conditions { get; set; } = [];
    public List<PblFormRuleAction> Actions { get; set; } = [];
}

public sealed class PblFormRuleCondition
{
    /// <summary>"field" (default) compares a field; "role" tests the user's role (then <see cref="Value"/> is the role name and <see cref="FieldName"/> is empty).</summary>
    public string Kind { get; set; } = "field";

    /// <summary>A <see cref="PblField.Name"/> on the form's table.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>One of: eq, ne, contains, notContains, startsWith, endsWith, isEmpty, isNotEmpty, gt, gte, lt, lte.</summary>
    public string Operator { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? ValueType { get; set; }

    /// <summary>A <see cref="PblField.Name"/> on the form's table, when comparing against another field's value.</summary>
    public string? ValueFieldName { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class PblFormRuleAction
{
    /// <summary>One of PowerBase's FormRuleActionType: Show, Hide, Enable, Disable, Require,
    /// NotRequired, ChangeLabel, SetColor, DisplayMessage, PreventSave.</summary>
    public string ActionType { get; set; } = string.Empty;

    /// <summary>Field, Section, Block, or Page.</summary>
    public string TargetType { get; set; } = "Field";

    /// <summary>A <see cref="PblFormElement.LogicalRef"/> within the same form — required when TargetType is "Field".</summary>
    public string? TargetElementRef { get; set; }

    /// <summary>A <see cref="PblFormSection.LogicalRef"/> within the same form — required when TargetType is "Section".</summary>
    public string? TargetSectionRef { get; set; }

    /// <summary>A <see cref="PblFormBlock.LogicalRef"/> within the same form — required when TargetType is "Block".</summary>
    public string? TargetBlockRef { get; set; }

    /// <summary>A <see cref="PblFormPage.LogicalRef"/> within the same form - required when TargetType is "Page" (a multi-page form).</summary>
    public string? TargetPageRef { get; set; }
    public string? ActionValue { get; set; }
    public int DisplayOrder { get; set; }

    /// <summary>"field" = copy another field's live value (then <see cref="SourceFieldName"/>); null = the literal <see cref="ActionValue"/>.</summary>
    public string? ValueSource { get; set; }
    public string? SourceFieldName { get; set; }

    /// <summary>For DisplayMessage: "notification", "modal" or "field"; severity "success", "info", "warning" or "error".</summary>
    public string? MessageType { get; set; }
    public string? MessageSeverity { get; set; }
}

public sealed class PblFormElement
{
    public string LogicalRef { get; set; } = string.Empty;

    /// <summary>One of PowerBase's valid ElementTypes: Field, StaticText, Divider, Button, Report.</summary>
    public string ElementType { get; set; } = "Field";

    /// <summary>A <see cref="PblField.Name"/> on the form's table — required when ElementType is "Field".</summary>
    public string? FieldName { get; set; }
    public string? ElementContent { get; set; }

    /// <summary>One of: Default, Custom, Hide.</summary>
    public string LabelMode { get; set; } = "Default";
    public string? CustomLabel { get; set; }
    public bool ShowOnAdd { get; set; } = true;
    public bool ShowOnEdit { get; set; } = true;
    public bool ShowOnView { get; set; } = true;

    /// <summary>One of: Auto, Half, Full, Fixed.</summary>
    public string WidthMode { get; set; } = "Auto";
    public int? WidthValue { get; set; }
    public string? HelpTextOverride { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsRequired { get; set; }
    public string? DisplayAs { get; set; }

    /// <summary>Elements that sat in the same Quickbase group share this ref (a <see cref="PblFormElement.LogicalRef"/> of the group); null = ungrouped.</summary>
    public string? GroupRef { get; set; }

    /// <summary>For ElementType "Report" (an embedded related-records grid): the PBL report to embed, the table it belongs to, and the source options.</summary>
    public string? EmbeddedReportRef { get; set; }
    public string? EmbeddedReportTableRef { get; set; }

    /// <summary>For a Field element that shows a Quickbase report-link field ("File records"): the child table it lists and that table's reference
    /// field. PowerBase makes its own link per relationship under another name, so the element is found through these.</summary>
    public string? ReportLinkChildTableRef { get; set; }
    public string? ReportLinkReferenceFieldName { get; set; }
    public bool ShowToolbar { get; set; } = true;
    public bool AllowCreateRecords { get; set; } = true;
    public bool OpenInSubForm { get; set; } = true;
}
