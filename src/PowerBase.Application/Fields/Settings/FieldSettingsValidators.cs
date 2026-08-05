using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Settings;

// ─── Text / Email / Phone ─────────────────────────────────────────────────────

public sealed class TextSettingsValidator : FieldSettingsValidatorBase<TextSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes =>
        ["Text", "TextMultiLine", "RichText", "Email", "Phone"];

    protected override IDictionary<string, string[]> ValidateTyped(TextSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Validation?.MaxLength is < 1)
            AddError(errors, "Settings.Validation.MaxLength", "MaxLength must be at least 1.");

        if (s.Validation?.Regex is string rx && !IsValidRegex(rx))
            AddError(errors, "Settings.Validation.Regex", $"'{rx}' is not a valid regular expression.");

        return errors;
    }
}

// ─── Number ───────────────────────────────────────────────────────────────────

public sealed class NumberSettingsValidator : FieldSettingsValidatorBase<NumberSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Number"];

    protected override IDictionary<string, string[]> ValidateTyped(NumberSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");

        if (s.Validation?.Min is decimal min && s.Validation?.Max is decimal max && min > max)
            AddError(errors, "Settings.Validation", "Min must not be greater than Max.");

        return errors;
    }
}

// ─── Currency ─────────────────────────────────────────────────────────────────

public sealed class CurrencySettingsValidator : FieldSettingsValidatorBase<CurrencySettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Currency"];

    protected override IDictionary<string, string[]> ValidateTyped(CurrencySettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Symbol is not null && s.Symbol.Length > 10)
            AddError(errors, "Settings.Symbol", "Currency symbol must be 10 characters or fewer.");

        if (s.Position is not null && !CurrencyPositions.All.Contains(s.Position))
            AddError(errors, "Settings.Position",
                $"Position must be one of: {string.Join(", ", CurrencyPositions.All)}.");

        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");

        return errors;
    }
}

// ─── Percent ──────────────────────────────────────────────────────────────────

public sealed class PercentSettingsValidator : FieldSettingsValidatorBase<PercentSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Percent"];

    protected override IDictionary<string, string[]> ValidateTyped(PercentSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");

        if (s.Validation?.Min is decimal min && s.Validation?.Max is decimal max && min > max)
            AddError(errors, "Settings.Validation", "Min must not be greater than Max.");

        return errors;
    }
}

// ─── Rating ───────────────────────────────────────────────────────────────────

public sealed class RatingSettingsValidator : FieldSettingsValidatorBase<RatingSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Rating"];

    protected override IDictionary<string, string[]> ValidateTyped(RatingSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Max is < 1 or > 20)
            AddError(errors, "Settings.Max", "Rating max must be between 1 and 20.");

        return errors;
    }
}

// ─── Date / DateTime ──────────────────────────────────────────────────────────

public sealed class DateSettingsValidator : FieldSettingsValidatorBase<DateSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Date", "DateTime"];

    private static readonly string[] ValidFormats =
    [
        "MM-DD-YYYY", "DD-MM-YYYY", "YYYY-MM-DD",
        "MM/DD/YYYY", "DD/MM/YYYY", "YYYY/MM/DD",
        "MMM D, YYYY", "D MMM YYYY",
        // DateTime extras
        "MM-DD-YYYY HH:mm", "DD-MM-YYYY HH:mm", "YYYY-MM-DD HH:mm",
        "MM/DD/YYYY HH:mm", "DD/MM/YYYY HH:mm",
    ];

    protected override IDictionary<string, string[]> ValidateTyped(DateSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Format is not null && !ValidFormats.Contains(s.Format))
            AddError(errors, "Settings.Format",
                $"Format '{s.Format}' is not a recognised date format.");

        return errors;
    }
}

// ─── Duration ─────────────────────────────────────────────────────────────────

public sealed class DurationSettingsValidator : FieldSettingsValidatorBase<DurationSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Duration"];

    protected override IDictionary<string, string[]> ValidateTyped(DurationSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Display is not null && !DurationDisplays.All.Contains(s.Display))
            AddError(errors, "Settings.Display",
                $"Display must be one of: {string.Join(", ", DurationDisplays.All)}.");

        if (s.Decimals is < 0 or > 6)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 6.");

        return errors;
    }
}

// ─── URL ──────────────────────────────────────────────────────────────────────

public sealed class UrlSettingsValidator : FieldSettingsValidatorBase<UrlSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Url"];

    protected override IDictionary<string, string[]> ValidateTyped(UrlSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Variant is not null && !UrlVariants.All.Contains(s.Variant))
            AddError(errors, "Settings.Variant",
                $"Variant must be one of: {string.Join(", ", UrlVariants.All)}.");

        if (s.Variant == UrlVariants.Formula && string.IsNullOrWhiteSpace(s.Template))
            AddError(errors, "Settings.Template",
                "A template is required for formula URL fields.");

        return errors;
    }
}

// ─── DateRange ────────────────────────────────────────────────────────────────

public sealed class DateRangeSettingsValidator : FieldSettingsValidatorBase<DateRangeSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["DateRange"];

    protected override IDictionary<string, string[]> ValidateTyped(DateRangeSettings s) =>
        new Dictionary<string, string[]>();
}

// ─── NumericRange ─────────────────────────────────────────────────────────────

public sealed class NumericRangeSettingsValidator : FieldSettingsValidatorBase<NumericRangeSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["NumericRange"];

    protected override IDictionary<string, string[]> ValidateTyped(NumericRangeSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");

        return errors;
    }
}

// ─── Report Link ─────────────────────────────────────────────────────────────

public sealed class ReportLinkSettingsValidator : FieldSettingsValidatorBase<ReportLinkSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["ReportLink"];

    protected override IDictionary<string, string[]> ValidateTyped(ReportLinkSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(s.TargetTablePublicId))
            AddError(errors, "Settings.TargetTablePublicId", "A target table is required.");

        if (s.TargetFid is null)
            AddError(errors, "Settings.TargetFid", "A target field is required.");

        if (s.ColumnWidth is < 20 or > 2000)
            AddError(errors, "Settings.ColumnWidth", "Column width must be between 20 and 2000.");

        return errors;
    }
}

// ─── Formula ──────────────────────────────────────────────────────────────────

public sealed class FormulaSettingsValidator : FieldSettingsValidatorBase<FormulaSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula"];

    // Shape-only validation here (no table schema). The expression is compiled
    // against the table's fields in the create/update handler and the validate API.
    protected override IDictionary<string, string[]> ValidateTyped(FormulaSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(s.ResultType))
            AddError(errors, "Settings.ResultType", "A formula result type is required.");
        else if (!FormulaResultTypes.All.Contains(s.ResultType))
            AddError(errors, "Settings.ResultType",
                $"Result type must be one of: {string.Join(", ", FormulaResultTypes.All)}.");

        return errors;
    }
}

// ─── Action Button ────────────────────────────────────────────────────────────

public sealed class ActionButtonSettingsValidator : FieldSettingsValidatorBase<ActionButtonSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["ActionButton"];

    // Shape-only validation here (no table schema — target/capture Fid existence and
    // formula expressions are checked against the table's fields in the create/update
    // handler, mirroring how Formula's Expression is compiled there).
    protected override IDictionary<string, string[]> ValidateTyped(ActionButtonSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(s.Variant))
        {
            AddError(errors, "Settings.Variant", "A button variant is required.");
            return errors;
        }
        if (!ActionButtonVariants.All.Contains(s.Variant))
        {
            AddError(errors, "Settings.Variant",
                $"Variant must be one of: {string.Join(", ", ActionButtonVariants.All)}.");
            return errors;
        }

        ValidateValueSource(s.ButtonLabel, "Settings.ButtonLabel", errors);
        ValidateValueSource(s.ButtonColor, "Settings.ButtonColor", errors);
        ValidateValueSource(s.FileName, "Settings.FileName", errors);
        ValidateValueSource(s.DefaultValue, "Settings.DefaultValue", errors);
        ValidateValueSource(s.Redirect, "Settings.Redirect", errors);
        if (s.RedirectMode is not null && !RedirectModes.All.Contains(s.RedirectMode))
            AddError(errors, "Settings.RedirectMode",
                $"RedirectMode must be one of: {string.Join(", ", RedirectModes.All)}.");
        ValidateValueSource(s.PasswordGate, "Settings.PasswordGate", errors);
        if (s.LinkExpiration?.Start is not null)
            ValidateValueSource(s.LinkExpiration.Start, "Settings.LinkExpiration.Start", errors);

        if (s.Variant is ActionButtonVariants.Signature or ActionButtonVariants.File or ActionButtonVariants.Prompt
            && s.CaptureFid is null)
            AddError(errors, "Settings.CaptureFid", "A capture field is required for this variant.");

        if (s.Variant == ActionButtonVariants.Prompt)
        {
            if (string.IsNullOrWhiteSpace(s.PromptType))
                AddError(errors, "Settings.PromptType", "A prompt type is required.");
            else if (!PromptTypes.All.Contains(s.PromptType))
                AddError(errors, "Settings.PromptType",
                    $"PromptType must be one of: {string.Join(", ", PromptTypes.All)}.");
            else if (s.PromptType == PromptTypes.FromField && s.PromptSourceFid is null)
                AddError(errors, "Settings.PromptSourceFid",
                    "A source field is required when PromptType is FromField.");
            else if (s.PromptType == PromptTypes.EnterData && (s.PromptOptions is null || s.PromptOptions.Length == 0))
                AddError(errors, "Settings.PromptOptions",
                    "At least one option is required when PromptType is EnterData.");
        }

        if (s.AddData is { Length: > 0 })
        {
            for (var i = 0; i < s.AddData.Length; i++)
            {
                var item = s.AddData[i];
                if (item.TargetFid is null)
                    AddError(errors, $"Settings.AddData[{i}].TargetFid", "A target field is required.");
                ValidateValueSource(item.Value, $"Settings.AddData[{i}].Value", errors);
            }
        }

        if (s.Variant == ActionButtonVariants.Data && (s.AddData is null || s.AddData.Length == 0))
            AddError(errors, "Settings.AddData", "A Data Button requires at least one field to write.");

        if (s.LinkExpiration is { } exp && exp.Minutes is < 1)
            AddError(errors, "Settings.LinkExpiration.Minutes", "Minutes must be at least 1.");

        if (s.PopupSize is { } size && (size.Width is < 100 || size.Height is < 100))
            AddError(errors, "Settings.PopupSize", "Popup width/height must be at least 100px.");

        return errors;
    }

    private static void ValidateValueSource(ValueSource? vs, string field, IDictionary<string, string[]> errors)
    {
        if (vs is null) return;
        if (string.IsNullOrWhiteSpace(vs.Kind))
        {
            AddError(errors, field, "A value source kind is required.");
            return;
        }
        if (!ValueSourceKinds.All.Contains(vs.Kind))
        {
            AddError(errors, field, $"Kind must be one of: {string.Join(", ", ValueSourceKinds.All)}.");
            return;
        }
        switch (vs.Kind)
        {
            case ValueSourceKinds.Field when vs.FieldFid is null:
                AddError(errors, field, "FieldFid is required when Kind is 'field'.");
                break;
            case ValueSourceKinds.Formula when string.IsNullOrWhiteSpace(vs.Formula):
                AddError(errors, field, "Formula is required when Kind is 'formula'.");
                break;
        }
    }
}
