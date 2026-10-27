namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>Plain-language warnings for the preview panel, and display text for before/after samples.</summary>
public static class FieldTypeChangeWarnings
{
    public static IReadOnlyList<string> Build(FieldTypeChangePlan plan, FieldTypeChangeImpact impact, FieldTypeChangeDryRunResult? dryRun)
    {
        var warnings = new List<string>();
        var kind = plan.Check.Kind;

        if (kind == ConversionKind.ScalarToFormula && dryRun is { NonEmpty: > 0 })
            warnings.Add($"All {dryRun.NonEmpty} stored value(s) will be discarded: a formula field computes its values. The formula starts empty.");
        else if (dryRun is { WillBecomeBlank: > 0 })
            warnings.Add($"{dryRun.WillBecomeBlank} of {dryRun.NonEmpty} value(s) can't be converted to the new type and will become blank.");

        if (kind == ConversionKind.FormulaToScalar)
            warnings.Add("The formula will be removed. Its current results are kept as ordinary values you can edit.");

        var capabilities = FieldGeneralSettingsCapability.Resolve(plan.TargetTypeCode, null);
        var targetIsFormula = FieldTypeConversionMatrix.IsFormula(plan.TargetTypeCode);
        if (plan.Field.IsUnique && dryRun is { DuplicateValues: > 0 })
            warnings.Add($"{dryRun.DuplicateValues} converted value(s) repeat, so \"Must be unique\" will be turned off.");
        else if (plan.Field.IsUnique && (targetIsFormula || capabilities?.Unique == false))
            warnings.Add("The new type can't be unique, so \"Must be unique\" will be turned off.");
        if (plan.Field.IsRequired && capabilities?.Required == false)
            warnings.Add("The new type can't be required, so \"Must be filled in\" will be turned off.");

        if (dryRun?.Choices is { Overflowed: true } choices)
            warnings.Add($"Only the first {SelectChoiceBuilder.MaxChoices} distinct values become choices; {choices.OverflowCount} more are kept on their records.");

        if (impact.BrokenFormulas.Count > 0)
            warnings.Add($"{impact.BrokenFormulas.Count} formula(s) won't work with the new type and will show blank until they're edited.");

        if (impact.Usage is { } usage && usage.Reports.Count > 0)
            warnings.Add($"Used in {usage.Reports.Count} report(s). Settings that don't fit the new type (totals, grouping, filters) are skipped and come back if you change the type back.");

        if (!string.IsNullOrWhiteSpace(plan.Field.DefaultValue) && FieldTypeChangeSettings.BuildDefaultValue(plan.Field, plan.TargetTypeCode, plan.ConversionContext) is null)
            warnings.Add("The default value can't be carried over and will be cleared.");

        return warnings;
    }

    /// <summary>A value as readable text (dates in the app's format, users as email, …).</summary>
    public static string? Display(object? value, string valueType, FieldValueConversionContext context) =>
        value is null ? null : FieldValueConverter.ToText(value, valueType, context, singleLine: false);
}
