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

public sealed class NumberSettings
{
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class CurrencySettings
{
    public string? Symbol { get; set; }
    /// <summary>"before" | "after".</summary>
    public string? Position { get; set; }
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class PercentSettings
{
    public int? Decimals { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class RatingSettings
{
    public int? Max { get; set; }
}

public sealed class DateSettings
{
    public string? Format { get; set; }
    public bool? DefaultToday { get; set; }
}

public sealed class DurationSettings
{
    /// <summary>One of <see cref="DurationDisplays"/>.</summary>
    public string? Display { get; set; }
    public int? Decimals { get; set; }
}

public sealed class UrlSettings
{
    /// <summary>"plain" | "formula" (formula variant deferred).</summary>
    public string? Variant { get; set; }
    public string? Template { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class TextSettings
{
    public ValidationSettings? Validation { get; set; }
}

public static class CurrencyPositions
{
    public const string Before = "before";
    public const string After = "after";
    public static readonly string[] All = [Before, After];
}

public static class UrlVariants
{
    public const string Plain = "plain";
    public const string Formula = "formula";
    public static readonly string[] All = [Plain, Formula];
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
    /// <summary>Optional child-record filter (serialized FilterGroup JSON) applied before aggregating.</summary>
    public string? FilterTree { get; set; }
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
