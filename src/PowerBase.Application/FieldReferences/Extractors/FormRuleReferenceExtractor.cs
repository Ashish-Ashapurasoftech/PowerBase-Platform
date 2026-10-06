using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>The fields a form rule depends on: its conditions (and the fields they compare
/// against), its expression-mode formula, the form element each action targets, and any formula
/// an action computes its value from. Conditions and elements store the field's per-table
/// <c>Fid</c> (migration 013); formulas are parsed against the table.</summary>
public static class FormRuleReferenceExtractor
{
    /// <param name="elementFids">FormElement.Id → the Fid of the field that element shows (from the rule's form layout).</param>
    public static void Extract(
        FormRule rule,
        IReadOnlyDictionary<long, long?> elementFids,
        TableFieldIndex table,
        FormulaEngine engine,
        FieldReferenceCollector collector)
    {
        foreach (var c in rule.Conditions)
        {
            collector.Add(table.IdOfFid(c.AppFieldId), FieldReferenceUsages.RuleCondition);
            collector.Add(table.IdOfFid(c.ValueFieldId), FieldReferenceUsages.RuleConditionValue);
            collector.Add(table.IdOfFid(c.ChangeFromValueFieldId), FieldReferenceUsages.RuleConditionValue);
            collector.Add(table.IdOfFid(c.ChangeToValueFieldId), FieldReferenceUsages.RuleConditionValue);
        }

        if (rule.IsExpressionMode)
            collector.AddAll(table.FieldsReadBy(engine, rule.ExpressionText), FieldReferenceUsages.RuleExpression);

        foreach (var a in rule.Actions)
        {
            if (a.TargetElementId is { } elementId && elementFids.TryGetValue(elementId, out var fid))
                collector.Add(table.IdOfFid(fid), FieldReferenceUsages.RuleTarget);

            if (a.IsExpressionValue)
                collector.AddAll(table.FieldsReadBy(engine, a.ActionValue), FieldReferenceUsages.RuleActionFormula);
        }
    }
}
