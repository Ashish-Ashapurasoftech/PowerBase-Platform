namespace PowerBase.Domain.FieldSettings;

/// <summary>
/// Typed contract for the per-field-type configuration stored in
/// <c>AppField.Settings</c> (an otherwise free-form JSON column). These POCOs
/// describe the canonical shape for each field-type group so the value can be
/// validated on the backend and rendered consistently on the frontend.
///
/// All members are nullable: settings are optional, and an absent member means
/// "use the platform default". Property names are serialized as camelCase to
/// match the Angular client (see <see cref="FieldSettingsJson"/>).
/// </summary>
public sealed class ValidationSettings
{
    /// <summary>UI-soft regex the value must match (format validation only).</summary>
    public string? Regex { get; set; }

    /// <summary>UI-soft maximum character length.</summary>
    public int? MaxLength { get; set; }

    /// <summary>UI-soft inclusive minimum (numeric types).</summary>
    public decimal? Min { get; set; }

    /// <summary>UI-soft inclusive maximum (numeric types).</summary>
    public decimal? Max { get; set; }
}

// ── Display trio shared by nearly every renderable field type ────────────────
// Not a nested object on the wire (kept flat on each settings class, matching the
// pre-existing ReportLinkSettings convention) — this is just a doc-comment anchor;
// each settings class below declares its own DisplayBold/NoWrap/ColumnWidth members.

public sealed class TextSettings
{
    public ValidationSettings? Validation { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

/// <summary>RichText's Behavior Settings are deliberately smaller than Text's — no
/// bold/no-wrap/regex (rich text already carries its own formatting).</summary>
public sealed class RichTextSettings
{
    public int? MaxLength { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class EmailSettings
{
    /// <summary>Leave empty to use the email address itself as the link text.</summary>
    public string? LinkText { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    /// <summary>Display the entire email address.</summary>
    public bool? ShowFullAddress { get; set; }
    /// <summary>Display just the part before "@".</summary>
    public bool? ShowBeforeAt { get; set; }
    /// <summary>Display just the part before the first "_".</summary>
    public bool? ShowBeforeUnderscore { get; set; }
}

public sealed class PhoneSettings
{
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── Select (choices themselves stay in General; these are Behavior only) ─────
// One shared shape for SingleSelect and MultiSelect (the validator has no way to know,
// at validation time, which of its SupportedTypeCodes triggered a given call — see
// FieldSettingsValidatorRegistry — so per-typeCode restriction of MaxLength to
// SingleSelect-only is enforced by the frontend panel, which simply never renders/sends
// it for MultiSelect).

public sealed class SelectSettingsBase
{
    public string[]? Options { get; set; }
    /// <summary>Max characters override. Meaningful for SingleSelect only.</summary>
    public int? MaxLength { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── Numeric family: Number / Currency / Percent / Rating ─────────────────────
// One shared shape for all four TypeCodes. "DisplayAs" is what drives the
// dynamic field-type switch on save (see NumericDisplayAs / UpdateFieldCommandHandler) —
// Symbol/Position are meaningful when DisplayAs = Currency, Max when DisplayAs = Rating.

public sealed class NumericSettings
{
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    public ValidationSettings? Validation { get; set; }

    /// <summary>One of <see cref="NumericDisplayAs"/>. Drives which TypeCode this field
    /// should have after save — see UpdateFieldCommandHandler.</summary>
    public string? DisplayAs { get; set; }

    /// <summary>Currency symbol (meaningful when DisplayAs = Currency).</summary>
    public string? Symbol { get; set; }
    /// <summary>One of <see cref="CurrencyPositions"/> (meaningful when DisplayAs = Currency).</summary>
    public string? Position { get; set; }
    /// <summary>Maximum stars, 1-20 (meaningful when DisplayAs = Rating).</summary>
    public int? Max { get; set; }

    /// <summary>Text appended to the field name to describe the values in this field.</summary>
    public string? UnitsDescription { get; set; }
    /// <summary>Treat blank values as 0 in calculations. Defaults to true when absent.</summary>
    public bool? TreatBlankAsZero { get; set; }
    public bool? ShowTotalInReports { get; set; }
    public bool? ShowAverageInReports { get; set; }

    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public static class NumericDisplayAs
{
    public const string Number = "Number";
    public const string Currency = "Currency";
    public const string Percent = "Percent";
    public const string Rating = "Rating";
    public static readonly string[] All = [Number, Currency, Percent, Rating];
}

public sealed class DateSettings
{
    public bool? ShowMonthName { get; set; }
    public bool? ShowDayOfWeek { get; set; }
    /// <summary>Don't show the year for dates that fall in the current year.</summary>
    public bool? HideYearIfCurrent { get; set; }
    /// <summary>DateTime only — checked by default. Ignored for plain Date.</summary>
    public bool? ShowTime { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

/// <summary>Settings for the Time (TimeOfDay) field type.</summary>
public sealed class TimeSettings
{
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    /// <summary>One of <see cref="TimeFormats"/>.</summary>
    public string? Format { get; set; }
    public bool? Use24Hour { get; set; }
}

public static class TimeFormats
{
    public const string HHMM = "HHMM";
    public const string HHMMSS = "HHMMSS";
    public static readonly string[] All = [HHMM, HHMMSS];
}

public sealed class DurationSettings
{
    /// <summary>One of <see cref="DurationDisplays"/>.</summary>
    public string? Display { get; set; }
    public int? Decimals { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class UrlSettings
{
    public ValidationSettings? Validation { get; set; }

    /// <summary>Leave empty to use the URL itself as the link text.</summary>
    public string? LinkText { get; set; }
    /// <summary>Don't show "http://" when showing the URL.</summary>
    public bool? HideProtocol { get; set; }
    /// <summary>Display as a button on forms and reports.</summary>
    public bool? DisplayAsButton { get; set; }
    /// <summary>Embed as an iframe in forms.</summary>
    public bool? EmbedAsIframe { get; set; }
    /// <summary>One of <see cref="UrlOpenTargets"/>.</summary>
    public string? OpenTarget { get; set; }

    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

/// <summary>
/// Configuration for a Formula URL field: a computed field (see PhysicalNaming.IsComputedTypeCode)
/// whose value is the Text result of evaluating <see cref="Template"/> against the record,
/// same as a Formula field — see FormulaProjector/FormulaTypeMap.FormulaUrlTemplate. Split out
/// from the plain Url field type (which used to carry this as a "formula variant") so the two
/// are independently selectable field types.
/// </summary>
public sealed class FormulaUrlSettings
{
    /// <summary>The Quickbase-style formula expression text; must evaluate to Text.</summary>
    public string? Template { get; set; }

    /// <summary>Leave empty to use the computed URL itself as the link text.</summary>
    public string? LinkText { get; set; }
    /// <summary>Don't show "http://" when showing the URL.</summary>
    public bool? HideProtocol { get; set; }
    /// <summary>Display as a button on forms and reports.</summary>
    public bool? DisplayAsButton { get; set; }
    /// <summary>Embed as an iframe in forms.</summary>
    public bool? EmbedAsIframe { get; set; }
    /// <summary>One of <see cref="UrlOpenTargets"/>.</summary>
    public string? OpenTarget { get; set; }

    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public static class UrlOpenTargets
{
    public const string SamePage = "SamePage";
    public const string NewWindow = "NewWindow";
    public const string Popup = "Popup";
    public static readonly string[] All = [SamePage, NewWindow, Popup];
}

public static class CurrencyPositions
{
    public const string Before = "before";
    public const string After = "after";
    public static readonly string[] All = [Before, After];
}

public static class DurationDisplays
{
    public static readonly string[] All =
    [
        "HHMM", "HHMMSS", "MM", "MMSS", "Smart",
        "Weeks", "Days", "Hours", "Minutes", "Seconds",
    ];
}

public sealed class DateRangeSettings
{
    /// <summary>Display format applied to both start and end dates (same options as DateSettings).</summary>
    public string? Format { get; set; }
}

public sealed class NumericRangeSettings
{
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
}

// ── Checkbox (Boolean) ────────────────────────────────────────────────────────

public sealed class BooleanSettings
{
    /// <summary>Show the value as "Yes"/"No" in reports instead of a checkbox glyph.</summary>
    public bool? ShowYesNo { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── File Attachment ───────────────────────────────────────────────────────────

public sealed class FileSettings
{
    /// <summary>Leave empty to use the file name as the link text.</summary>
    public string? LinkText { get; set; }
    /// <summary>Show .jpg/.png/.gif files as images in forms.</summary>
    public bool? ShowImagePreview { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── Address ────────────────────────────────────────────────────────────────────

public sealed class AddressSettings
{
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── User / MultiUser ──────────────────────────────────────────────────────────

public sealed class UserFieldSettings
{
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    /// <summary>One of <see cref="UserDisplayAsOptions"/>.</summary>
    public string? DisplayAs { get; set; }
    /// <summary>One of <see cref="UserSetModes"/>.</summary>
    public string? UserSetMode { get; set; }
    /// <summary>PublicIds of the allowed users when UserSetMode = Custom.</summary>
    public string[]? CustomUserPublicIds { get; set; }
}

public sealed class MultiUserFieldSettings
{
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    /// <summary>One of <see cref="UserSetModes"/>.</summary>
    public string? UserSetMode { get; set; }
    public string[]? CustomUserPublicIds { get; set; }
}

public static class UserDisplayAsOptions
{
    public const string FullName = "FullName";
    public const string LastFirst = "LastFirst";
    public const string UserName = "UserName";
    public static readonly string[] All = [FullName, LastFirst, UserName];
}

public static class UserSetModes
{
    public const string Default = "Default";
    public const string Custom = "Custom";
    public static readonly string[] All = [Default, Custom];
}

/// <summary>
/// Configuration for a Formula field: the declared result type and the expression
/// text. A Formula field is computed at read time and stores no physical column.
/// </summary>
public sealed class FormulaSettings
{
    /// <summary>One of <see cref="FormulaResultTypes"/>; maps to the engine's FormulaType.</summary>
    public string? ResultType { get; set; }

    /// <summary>The Quickbase-style formula expression text.</summary>
    public string? Expression { get; set; }

    /// <summary>
    /// When true (ResultType = Text only), the formula's computed value is a button
    /// descriptor rather than plain text: either a bare Fid ("42") or a JSON object
    /// ("{"fid":42,"label":"...","color":"...","enabled":false}") naming an ActionButton
    /// field on this table whose full configured behavior the rendered button inherits,
    /// with any present JSON keys overriding that button's own settings. This is how a
    /// formula can pick WHICH button to show, and how (color/enabled/label), with full
    /// conditional logic (Field-Type spec Rule 3).
    /// </summary>
    public bool? ReturnsButton { get; set; }
}

public static class FormulaResultTypes
{
    public const string Text = "Text";
    public const string Number = "Number";
    public const string Date = "Date";
    public const string DateTime = "DateTime";
    public const string Duration = "Duration";
    public const string Bool = "Bool";
    public const string User = "User";

    public static readonly string[] All = [Text, Number, Date, DateTime, Duration, Bool, User];
}

// ── Formula field-type family: one dedicated settings class per Formula_{X}
// TypeCode (Formula_Text, Formula_Number, …), each carrying its own Expression
// plus exactly the Behavior Settings for that result type — distinct from (not a
// copy of) the corresponding plain type's settings class, since a computed value
// has no user-entered validation and several variants deliberately drop/add
// fields relative to their plain counterpart (see FIELD_TYPES spec). The legacy
// generic FormulaSettings/FormulaResultTypes above are unchanged and still serve
// the bare "Formula" TypeCode.

public sealed class FormulaTextSettings
{
    public string? Expression { get; set; }
    public int? MaxLength { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    /// <summary>Same meaning as <see cref="FormulaSettings.ReturnsButton"/>.</summary>
    public bool? ReturnsButton { get; set; }
}

public sealed class FormulaNumericSettings
{
    public string? Expression { get; set; }
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    /// <summary>One of <see cref="NumberDisplayPatterns"/> — digit-grouping style
    /// (Standard 3-digit vs Indian lakh/crore), independent of Separator.</summary>
    public string? DigitGrouping { get; set; }
    /// <summary>One of <see cref="NumericDisplayAs"/>.</summary>
    public string? DisplayAs { get; set; }
    public string? Symbol { get; set; }
    /// <summary>One of <see cref="FormulaCurrencyPositions"/> — three options here (the plain
    /// Numeric family's <see cref="CurrencyPositions"/> only has two), per spec: "before number,
    /// between '-' and the number, after number".</summary>
    public string? Position { get; set; }
    /// <summary>Maximum stars, 1-20 (DisplayAs = Rating).</summary>
    public int? Max { get; set; }
    public string? UnitsDescription { get; set; }
    public bool? ShowTotalInReports { get; set; }
    public bool? ShowAverageInReports { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

/// <summary>Formula_Number's currency-symbol position — three options, unlike the plain Numeric
/// family's two (<see cref="CurrencyPositions"/>): "before number, between '-' and the number,
/// after number" (spec, Formula Fields category, item 21).</summary>
public static class FormulaCurrencyPositions
{
    public const string Before = "before";
    /// <summary>Symbol sits between the negative sign and the digits, e.g. "-$100".</summary>
    public const string BetweenSignAndNumber = "betweenSignAndNumber";
    public const string After = "after";
    public static readonly string[] All = [Before, BetweenSignAndNumber, After];
}

public static class NumberDisplayPatterns
{
    public const string Standard = "Standard";
    public const string Indian = "Indian";
    public static readonly string[] All = [Standard, Indian];
}

/// <summary>Shared by Formula_Date and Formula_DateTime — ShowTime only meaningful for the latter.</summary>
public sealed class FormulaDateSettings
{
    public string? Expression { get; set; }
    public bool? ShowMonthName { get; set; }
    public bool? ShowDayOfWeek { get; set; }
    public bool? HideYearIfCurrent { get; set; }
    /// <summary>Formula_DateTime only. Ignored for Formula_Date.</summary>
    public bool? ShowTime { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaTimeSettings
{
    public string? Expression { get; set; }
    /// <summary>One of <see cref="TimeFormats"/>.</summary>
    public string? Format { get; set; }
    public bool? Use24Hour { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaDurationSettings
{
    public string? Expression { get; set; }
    /// <summary>One of <see cref="DurationDisplays"/>.</summary>
    public string? Display { get; set; }
    public bool? ShowTotalInReports { get; set; }
    public bool? ShowAverageInReports { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaBooleanSettings
{
    public string? Expression { get; set; }
    /// <summary>Show a totals row (count of true) in reports.</summary>
    public bool? ShowTotalsRow { get; set; }
    public bool? ShowYesNo { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaPhoneSettings
{
    public string? Expression { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaEmailSettings
{
    public string? Expression { get; set; }
    public string? LinkText { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
    public bool? ShowFullAddress { get; set; }
    public bool? ShowBeforeAt { get; set; }
    public bool? ShowBeforeUnderscore { get; set; }
}

public sealed class FormulaUserSettings
{
    public string? Expression { get; set; }
    /// <summary>One of <see cref="UserDisplayAsOptions"/>.</summary>
    public string? DisplayAs { get; set; }
    public bool? DisplayBold { get; set; }
    public bool? NoWrap { get; set; }
    public int? ColumnWidth { get; set; }
}

public sealed class FormulaRichTextSettings
{
    public string? Expression { get; set; }
    public int? MaxLength { get; set; }
    public int? ColumnWidth { get; set; }
}

// ── Relationship field settings ──────────────────────────────────────────────

/// <summary>
/// Settings for a Reference field (lives on the child table). A Reference is a physical
/// BIGINT column holding the parent row Id; these settings record which relationship and
/// parent table it belongs to so the picker and projector can resolve it.
/// </summary>
public sealed class ReferenceSettings
{
    public long? RelationshipId { get; set; }
    /// <summary>The parent table's <see cref="Entities.AppTable.Id"/>.</summary>
    public long? ParentTableId { get; set; }
}

/// <summary>
/// Settings for a Lookup field (lives on the child table). Pulls one parent field's value
/// onto each child via the reference. Computed at read time, no physical column.
/// Field references are <c>Fid</c> values (the per-table field number used in physical
/// columns <c>f_{fid}</c>), matching the data/report plane.
/// </summary>
public sealed class LookupSettings
{
    public long? RelationshipId { get; set; }
    /// <summary>The child reference field's Fid (its <c>f_{fid}</c> column holds the parent row Id).</summary>
    public int? ReferenceFid { get; set; }
    /// <summary>The parent table's <see cref="Entities.AppTable.Id"/>.</summary>
    public long? SourceTableId { get; set; }
    /// <summary>The parent field's Fid whose value is pulled down.</summary>
    public int? SourceFid { get; set; }
    /// <summary>The source field's TypeCode, captured at creation so the value can be rendered/typed
    /// without a cross-table lookup (e.g. for formula type-mapping).</summary>
    public string? SourceTypeCode { get; set; }
    /// <summary>When the source field is a composite Address field, the JSON sub-key to pull out
    /// of its value (one of <see cref="AddressSubFields.All"/>) instead of the whole address —
    /// e.g. just the city. Null for an ordinary (non-Address, or whole-Address) lookup.</summary>
    public string? SourceSubField { get; set; }
}

/// <summary>
/// Settings for a Summary field (lives on the parent table). Aggregates related child
/// records back up onto the parent. Computed at read time, no physical column.
/// </summary>
public sealed class SummarySettings
{
    public long? RelationshipId { get; set; }
    /// <summary>The child table's <see cref="Entities.AppTable.Id"/>.</summary>
    public long? ChildTableId { get; set; }
    /// <summary>The child reference field's Fid that points back to this parent.</summary>
    public int? ReferenceFid { get; set; }
    /// <summary>One of <see cref="SummaryFunctions"/>.</summary>
    public string? Function { get; set; }
    /// <summary>The child field's Fid to aggregate; null ⇒ Count.</summary>
    public int? TargetFid { get; set; }
    /// <summary>The target field's TypeCode (for Min/Max result typing/rendering); null for Count.</summary>
    public string? TargetTypeCode { get; set; }
    /// <summary>When the target field is a composite Address field, the JSON sub-key to aggregate
    /// (one of <see cref="AddressSubFields.All"/>) instead of the whole address. Only meaningful
    /// with Count/Exists/Min/Max — Sum/Avg over an address sub-value (always text) makes no sense
    /// and is rejected at creation time.</summary>
    public string? TargetSubField { get; set; }
    /// <summary>Optional child-record filter (serialized FilterGroup JSON) applied before aggregating.</summary>
    public string? FilterTree { get; set; }
}

/// <summary>The JSON keys an Address field's composite value is stored under (confirmed real
/// shape, matching both the Angular <c>AddressValue</c> interface and the existing report-filter
/// <c>SubField</c> mechanism in <c>ReportDefinition.cs</c>/<c>RecordRepository</c>).</summary>
public static class AddressSubFields
{
    public const string Street1 = "street1";
    public const string Street2 = "street2";
    public const string City = "city";
    public const string State = "state";
    public const string Zip = "zip";
    public const string Country = "country";

    public static readonly string[] All = [Street1, Street2, City, State, Zip, Country];
}

/// <summary>
/// Settings for a Report Link field. A Report Link is a value-match hyperlink that opens a
/// filtered view of a target table — no physical column, not projected by any projector.
/// Target can be in any app in the same tenant. SourceFid is null for "current record Id".
/// </summary>
public sealed class ReportLinkSettings
{
    /// <summary>When auto-created for a relationship, the relationship's numeric Id (for the detail page to match it).</summary>
    public long? RelationshipId { get; set; }
    /// <summary>The target app's PublicId (any same-tenant app).</summary>
    public string? TargetAppPublicId { get; set; }
    /// <summary>The target table's PublicId.</summary>
    public string? TargetTablePublicId { get; set; }
    /// <summary>The target table's field Fid to match against.</summary>
    public int? TargetFid { get; set; }
    /// <summary>The source field's Fid on THIS table whose value is matched. Null = current record Id.</summary>
    public int? SourceFid { get; set; }
    /// <summary>The hyperlink label text shown to the user.</summary>
    public string? LinkText { get; set; }
    /// <summary>When true the link opens in a new browser tab.</summary>
    public bool? OpenInNewWindow { get; set; }
    /// <summary>When true the match is case-sensitive / exact.</summary>
    public bool? ValueMatchExact { get; set; }
    /// <summary>When true render the link text in bold.</summary>
    public bool? DisplayBold { get; set; }
    /// <summary>When true disable wrapping on the cell.</summary>
    public bool? NoWrap { get; set; }
    /// <summary>Optional fixed column width in pixels for the grid.</summary>
    public int? ColumnWidth { get; set; }
}

public static class SummaryFunctions
{
    public const string Count = "Count";
    /// <summary>True/False — whether any related child records exist.</summary>
    public const string Exists = "Exists";
    public const string Sum = "Sum";
    public const string Avg = "Avg";
    public const string Min = "Min";
    public const string Max = "Max";

    public static readonly string[] All = [Count, Exists, Sum, Avg, Min, Max];
}

// ── Action Button field settings ────────────────────────────────────────────

/// <summary>
/// A recurring "value from data / field / formula" slot used throughout Action Button
/// settings (label, color, filename, prompt default, add-data values, redirect, gates).
/// Exactly one of <see cref="Data"/>/<see cref="FieldFid"/>/<see cref="Formula"/> is
/// meaningful, selected by <see cref="Kind"/>.
/// </summary>
public sealed class ValueSource
{
    /// <summary>One of <see cref="ValueSourceKinds"/>.</summary>
    public string? Kind { get; set; }
    /// <summary>Static value (Kind = "data"). Also used as a static color/URL/password.</summary>
    public string? Data { get; set; }
    /// <summary>Fid of another field on the same table to read the value from (Kind = "field").</summary>
    public int? FieldFid { get; set; }
    /// <summary>Formula expression text, evaluated against the current record (Kind = "formula").</summary>
    public string? Formula { get; set; }
}

public static class ValueSourceKinds
{
    public const string Data = "data";
    public const string Field = "field";
    public const string Formula = "formula";
    public static readonly string[] All = [Data, Field, Formula];
}

/// <summary>One "set this field to this value" instruction, used by Add Data to Capture
/// and (in its entirety) by the Data Button variant.</summary>
public sealed class AddDataItem
{
    /// <summary>The Fid of the field being written.</summary>
    public int? TargetFid { get; set; }
    public ValueSource? Value { get; set; }
}

/// <summary>Server-side-enforced expiration window for a button link.</summary>
public sealed class LinkExpirationSettings
{
    /// <summary>Static timestamp or formula (e.g. Now()) marking the start of the window.</summary>
    public ValueSource? Start { get; set; }
    /// <summary>Minutes after Start during which the button remains valid.</summary>
    public int? Minutes { get; set; }
}

/// <summary>Captures device geolocation into a field, optionally restricted to a state.</summary>
public sealed class LocationCaptureSettings
{
    public int? TargetFid { get; set; }
    public string? RestrictToState { get; set; }
}

public sealed class PopupSizeSettings
{
    public int? Width { get; set; }
    public int? Height { get; set; }
}

/// <summary>
/// Configuration for an Action Button field (Signature | File | Prompt | Data variant,
/// selected by <see cref="Variant"/>). Computed / non-stored — the button's own value is
/// never persisted; clicking it writes to other fields via the InvokeButtonAction endpoint.
/// </summary>
public sealed class ActionButtonSettings
{
    /// <summary>One of <see cref="ActionButtonVariants"/>.</summary>
    public string? Variant { get; set; }

    public ValueSource? ButtonLabel { get; set; }
    /// <summary>Static color (hex/name) or a formula, per <see cref="ValueSource"/>.</summary>
    public ValueSource? ButtonColor { get; set; }

    /// <summary>Where the captured signature / file / prompt value is stored (Signature/File/Prompt).</summary>
    public int? CaptureFid { get; set; }
    /// <summary>Stored file name (Signature/File). Blank → random (signature) / original (file).</summary>
    public ValueSource? FileName { get; set; }
    /// <summary>Optional field to record the server time of the click.</summary>
    public int? TimestampFid { get; set; }

    /// <summary>One of <see cref="PromptTypes"/> (Prompt only).</summary>
    public string? PromptType { get; set; }
    /// <summary>Source multichoice field Fid when PromptType = FromField.</summary>
    public int? PromptSourceFid { get; set; }
    /// <summary>Hand-built options list when PromptType = EnterData.</summary>
    public string[]? PromptOptions { get; set; }
    /// <summary>Pre-filled prompt value (Prompt only).</summary>
    public ValueSource? DefaultValue { get; set; }

    /// <summary>Fields to set on click, beyond the Capture field. The entire configuration
    /// for a Data Button (no capture UI).</summary>
    public AddDataItem[]? AddData { get; set; }

    /// <summary>Static URL or formula. Blank → in-place refresh (no navigation).</summary>
    public ValueSource? Redirect { get; set; }
    /// <summary>One of <see cref="RedirectModes"/> — how a resolved Redirect URL is opened.
    /// Only meaningful when Redirect is set; null/absent defaults to SameWindow. Distinct
    /// from capture popups (Signature/File/Prompt), which always render in a dialog
    /// regardless of this setting.</summary>
    public string? RedirectMode { get; set; }

    // ── Advanced (light) ──────────────────────────────────────────────────────
    public LinkExpirationSettings? LinkExpiration { get; set; }
    /// <summary>Boolean field Fid that must evaluate true at click time for the action to proceed.</summary>
    public int? BoolGateFid { get; set; }
    public LocationCaptureSettings? LocationCapture { get; set; }
    /// <summary>Field to save the caller's server-observed IP address into.</summary>
    public int? IpCaptureFid { get; set; }
    /// <summary>Expected password — static, formula, or read from another field.</summary>
    public ValueSource? PasswordGate { get; set; }
    public PopupSizeSettings? PopupSize { get; set; }
}

public static class ActionButtonVariants
{
    public const string Signature = "Signature";
    public const string File = "File";
    public const string Prompt = "Prompt";
    public const string Data = "Data";
    public static readonly string[] All = [Signature, File, Prompt, Data];
}

public static class PromptTypes
{
    public const string FromField = "FromField";
    public const string EnterData = "EnterData";
    public const string FreeText = "FreeText";
    public static readonly string[] All = [FromField, EnterData, FreeText];
}

/// <summary>
/// Navigation & Save-State Behavior for URL-type actions (Field-Type spec, July-2
/// addendum). Action Button captures (Signature/File/Prompt) always render in a popup
/// regardless of this setting — these modes apply only to a resolved Redirect URL.
/// </summary>
public static class RedirectModes
{
    /// <summary>Opens in a separate window/tab; the current page never navigates away. No unsaved-changes risk.</summary>
    public const string NewWindow = "NewWindow";
    /// <summary>Navigates the current window. Real unsaved-changes risk — the client must warn/block.</summary>
    public const string SameWindow = "SameWindow";
    public static readonly string[] All = [NewWindow, SameWindow];
}
