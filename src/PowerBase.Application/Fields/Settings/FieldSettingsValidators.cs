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
