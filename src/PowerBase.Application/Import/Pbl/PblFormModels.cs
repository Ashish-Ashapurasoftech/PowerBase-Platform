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

    /// <summary>Populated by the Form Rules slice; always empty until then.</summary>
    public List<PblFormRule> Rules { get; set; } = [];
}

public sealed class PblFormSection
{
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

    /// <summary>Field, Section, or Block.</summary>
    public string TargetType { get; set; } = "Field";

    /// <summary>A <see cref="PblFormElement.LogicalRef"/> within the same form — required when TargetType is "Field".</summary>
    public string? TargetElementRef { get; set; }

    /// <summary>A <see cref="PblFormSection.LogicalRef"/> within the same form — required when TargetType is "Section".</summary>
    public string? TargetSectionRef { get; set; }

    /// <summary>A <see cref="PblFormBlock.LogicalRef"/> within the same form — required when TargetType is "Block".</summary>
    public string? TargetBlockRef { get; set; }
    public string? ActionValue { get; set; }
    public int DisplayOrder { get; set; }
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
}
