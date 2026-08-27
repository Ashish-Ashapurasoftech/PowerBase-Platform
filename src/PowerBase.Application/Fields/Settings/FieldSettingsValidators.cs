using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Settings;

// ─── Text ───────────────────────────────────────────────────────────────────

public sealed class TextSettingsValidator : FieldSettingsValidatorBase<TextSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes =>
        ["Text", "TextMultiLine"];

    protected override IDictionary<string, string[]> ValidateTyped(TextSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Validation?.MaxLength is < 1)
            AddError(errors, "Settings.Validation.MaxLength", "MaxLength must be at least 1.");

        if (s.Validation?.Regex is string rx && !IsValidRegex(rx))
            AddError(errors, "Settings.Validation.Regex", $"'{rx}' is not a valid regular expression.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── RichText ─────────────────────────────────────────────────────────────────

public sealed class RichTextSettingsValidator : FieldSettingsValidatorBase<RichTextSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["RichText"];

    protected override IDictionary<string, string[]> ValidateTyped(RichTextSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.MaxLength is < 1)
            AddError(errors, "Settings.MaxLength", "MaxLength must be at least 1.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── Email ────────────────────────────────────────────────────────────────────

public sealed class EmailSettingsValidator : FieldSettingsValidatorBase<EmailSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Email"];

    protected override IDictionary<string, string[]> ValidateTyped(EmailSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── Phone ────────────────────────────────────────────────────────────────────

public sealed class PhoneSettingsValidator : FieldSettingsValidatorBase<PhoneSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Phone"];

    protected override IDictionary<string, string[]> ValidateTyped(PhoneSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── Select (SingleSelect / MultiSelect) ───────────────────────────────────────

public sealed class SelectSettingsValidator : FieldSettingsValidatorBase<SelectSettingsBase>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["SingleSelect", "MultiSelect"];

    protected override IDictionary<string, string[]> ValidateTyped(SelectSettingsBase s)
    {
        var errors = new Dictionary<string, string[]>();

        // MaxLength is only meaningful for SingleSelect — the frontend panel never renders/sends
        // it for MultiSelect. The validator itself is registered for both typeCodes and (per
        // FieldSettingsValidatorRegistry) has no way to tell which one triggered a given call, so
        // it's just range-checked here whenever present, regardless of which type sent it.
        if (s.MaxLength is < 1)
            AddError(errors, "Settings.MaxLength", "MaxLength must be at least 1.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── Numeric family: Number / Currency / Percent / Rating ──────────────────────

public sealed class NumericSettingsValidator : FieldSettingsValidatorBase<NumericSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Number", "Currency", "Percent", "Rating"];

    protected override IDictionary<string, string[]> ValidateTyped(NumericSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");

        if (s.Validation?.Min is decimal min && s.Validation?.Max is decimal max && min > max)
            AddError(errors, "Settings.Validation", "Min must not be greater than Max.");

        if (s.DisplayAs is not null && !NumericDisplayAs.All.Contains(s.DisplayAs))
            AddError(errors, "Settings.DisplayAs",
                $"DisplayAs must be one of: {string.Join(", ", NumericDisplayAs.All)}.");

        if (s.Symbol is not null && s.Symbol.Length > 10)
            AddError(errors, "Settings.Symbol", "Currency symbol must be 10 characters or fewer.");

        if (s.Position is not null && !CurrencyPositions.All.Contains(s.Position))
            AddError(errors, "Settings.Position",
                $"Position must be one of: {string.Join(", ", CurrencyPositions.All)}.");

        if (s.Max is < 1 or > 20)
            AddError(errors, "Settings.Max", "Rating max must be between 1 and 20.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── Date / DateTime ──────────────────────────────────────────────────────────

public sealed class DateSettingsValidator : FieldSettingsValidatorBase<DateSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Date", "DateTime"];

    protected override IDictionary<string, string[]> ValidateTyped(DateSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── Time (TimeOfDay) ───────────────────────────────────────────────────────────

public sealed class TimeSettingsValidator : FieldSettingsValidatorBase<TimeSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Time"];

    protected override IDictionary<string, string[]> ValidateTyped(TimeSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.Format is not null && !TimeFormats.All.Contains(s.Format))
            AddError(errors, "Settings.Format",
                $"Format must be one of: {string.Join(", ", TimeFormats.All)}.");

        ValidateColumnWidth(s.ColumnWidth, errors);

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

        ValidateColumnWidth(s.ColumnWidth, errors);

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

        if (s.OpenTarget is not null && !UrlOpenTargets.All.Contains(s.OpenTarget))
            AddError(errors, "Settings.OpenTarget",
                $"OpenTarget must be one of: {string.Join(", ", UrlOpenTargets.All)}.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── Formula URL ──────────────────────────────────────────────────────────────

public sealed class FormulaUrlSettingsValidator : FieldSettingsValidatorBase<FormulaUrlSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Url"];

    // Shape-only validation here (no table schema) — matches FormulaSettingsValidator's
    // approach; the expression is compiled/validated live via the frontend's
    // /formula/validate call, same as every other Formula-family field.
    protected override IDictionary<string, string[]> ValidateTyped(FormulaUrlSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(s.Template))
            AddError(errors, "Settings.Template", "A formula template is required.");

        if (s.OpenTarget is not null && !UrlOpenTargets.All.Contains(s.OpenTarget))
            AddError(errors, "Settings.OpenTarget",
                $"OpenTarget must be one of: {string.Join(", ", UrlOpenTargets.All)}.");

        ValidateColumnWidth(s.ColumnWidth, errors);

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

// ─── Checkbox (Boolean) ───────────────────────────────────────────────────────

public sealed class BooleanSettingsValidator : FieldSettingsValidatorBase<BooleanSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Boolean"];

    protected override IDictionary<string, string[]> ValidateTyped(BooleanSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── File Attachment ──────────────────────────────────────────────────────────

public sealed class FileSettingsValidator : FieldSettingsValidatorBase<FileSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["File"];

    protected override IDictionary<string, string[]> ValidateTyped(FileSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── Address ──────────────────────────────────────────────────────────────────

public sealed class AddressSettingsValidator : FieldSettingsValidatorBase<AddressSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Address"];

    protected override IDictionary<string, string[]> ValidateTyped(AddressSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── User ─────────────────────────────────────────────────────────────────────

public sealed class UserFieldSettingsValidator : FieldSettingsValidatorBase<UserFieldSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["User"];

    protected override IDictionary<string, string[]> ValidateTyped(UserFieldSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.DisplayAs is not null && !UserDisplayAsOptions.All.Contains(s.DisplayAs))
            AddError(errors, "Settings.DisplayAs",
                $"DisplayAs must be one of: {string.Join(", ", UserDisplayAsOptions.All)}.");

        if (s.UserSetMode is not null && !UserSetModes.All.Contains(s.UserSetMode))
            AddError(errors, "Settings.UserSetMode",
                $"UserSetMode must be one of: {string.Join(", ", UserSetModes.All)}.");

        if (s.UserSetMode == UserSetModes.Custom && (s.CustomUserPublicIds is null || s.CustomUserPublicIds.Length == 0))
            AddError(errors, "Settings.CustomUserPublicIds",
                "At least one user is required when UserSetMode is Custom.");

        ValidateColumnWidth(s.ColumnWidth, errors);

        return errors;
    }
}

// ─── MultiUser ────────────────────────────────────────────────────────────────

public sealed class MultiUserFieldSettingsValidator : FieldSettingsValidatorBase<MultiUserFieldSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["MultiUser"];

    protected override IDictionary<string, string[]> ValidateTyped(MultiUserFieldSettings s)
    {
        var errors = new Dictionary<string, string[]>();

        if (s.UserSetMode is not null && !UserSetModes.All.Contains(s.UserSetMode))
            AddError(errors, "Settings.UserSetMode",
                $"UserSetMode must be one of: {string.Join(", ", UserSetModes.All)}.");

        if (s.UserSetMode == UserSetModes.Custom && (s.CustomUserPublicIds is null || s.CustomUserPublicIds.Length == 0))
            AddError(errors, "Settings.CustomUserPublicIds",
                "At least one user is required when UserSetMode is Custom.");

        ValidateColumnWidth(s.ColumnWidth, errors);

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

        ValidateColumnWidth(s.ColumnWidth, errors);

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

// ─── Formula field-type family (Formula_Text, Formula_Number, …) ──────────────
// Shape-only validation, same rationale as FormulaSettingsValidator/
// FormulaUrlSettingsValidator: the expression is compiled/type-checked live via
// the frontend's /formula/validate call, not re-parsed here.

public sealed class FormulaTextSettingsValidator : FieldSettingsValidatorBase<FormulaTextSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Text"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaTextSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.MaxLength is < 1)
            AddError(errors, "Settings.MaxLength", "MaxLength must be at least 1.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaNumericSettingsValidator : FieldSettingsValidatorBase<FormulaNumericSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Number"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaNumericSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.Decimals is < 0 or > 10)
            AddError(errors, "Settings.Decimals", "Decimals must be between 0 and 10.");
        if (s.DigitGrouping is not null && !NumberDisplayPatterns.All.Contains(s.DigitGrouping))
            AddError(errors, "Settings.DigitGrouping",
                $"DigitGrouping must be one of: {string.Join(", ", NumberDisplayPatterns.All)}.");
        if (s.DisplayAs is not null && !NumericDisplayAs.All.Contains(s.DisplayAs))
            AddError(errors, "Settings.DisplayAs",
                $"DisplayAs must be one of: {string.Join(", ", NumericDisplayAs.All)}.");
        if (s.Symbol is not null && s.Symbol.Length > 10)
            AddError(errors, "Settings.Symbol", "Currency symbol must be 10 characters or fewer.");
        if (s.Position is not null && !FormulaCurrencyPositions.All.Contains(s.Position))
            AddError(errors, "Settings.Position",
                $"Position must be one of: {string.Join(", ", FormulaCurrencyPositions.All)}.");
        if (s.Max is < 1 or > 20)
            AddError(errors, "Settings.Max", "Rating max must be between 1 and 20.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaDateSettingsValidator : FieldSettingsValidatorBase<FormulaDateSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Date", "Formula_DateTime"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaDateSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaTimeSettingsValidator : FieldSettingsValidatorBase<FormulaTimeSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Time"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaTimeSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.Format is not null && !TimeFormats.All.Contains(s.Format))
            AddError(errors, "Settings.Format",
                $"Format must be one of: {string.Join(", ", TimeFormats.All)}.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaDurationSettingsValidator : FieldSettingsValidatorBase<FormulaDurationSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Duration"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaDurationSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.Display is not null && !DurationDisplays.All.Contains(s.Display))
            AddError(errors, "Settings.Display",
                $"Display must be one of: {string.Join(", ", DurationDisplays.All)}.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaBooleanSettingsValidator : FieldSettingsValidatorBase<FormulaBooleanSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Bool"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaBooleanSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaPhoneSettingsValidator : FieldSettingsValidatorBase<FormulaPhoneSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Phone"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaPhoneSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaEmailSettingsValidator : FieldSettingsValidatorBase<FormulaEmailSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_Email"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaEmailSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaUserSettingsValidator : FieldSettingsValidatorBase<FormulaUserSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_User"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaUserSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.DisplayAs is not null && !UserDisplayAsOptions.All.Contains(s.DisplayAs))
            AddError(errors, "Settings.DisplayAs",
                $"DisplayAs must be one of: {string.Join(", ", UserDisplayAsOptions.All)}.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

public sealed class FormulaRichTextSettingsValidator : FieldSettingsValidatorBase<FormulaRichTextSettings>
{
    public override IReadOnlyList<string> SupportedTypeCodes => ["Formula_RichText"];

    protected override IDictionary<string, string[]> ValidateTyped(FormulaRichTextSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(s.Expression))
            AddError(errors, "Settings.Expression", "A formula expression is required.");
        if (s.MaxLength is < 1)
            AddError(errors, "Settings.MaxLength", "MaxLength must be at least 1.");
        ValidateColumnWidth(s.ColumnWidth, errors);
        return errors;
    }
}

// ─── Action Button ────────────────────────────────────────────────────────────

public sealed class ActionButtonSettingsValidator : FieldSettingsValidatorBase<ActionButtonSettings>
{
    // Covers both shapes core.FieldType can have across tenant databases: the generic
    // 'ActionButton' row some tenants were migrated to, and the four original per-variant
    // rows (ActionButton_Signature/File/Prompt/Data) other tenants still have — see
    // PhysicalNaming.IsActionButtonTypeCode for the full explanation.
    public override IReadOnlyList<string> SupportedTypeCodes =>
        ["ActionButton", "ActionButton_Signature", "ActionButton_File", "ActionButton_Prompt", "ActionButton_Data"];

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
