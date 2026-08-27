using System.Text.Json;
using PowerBase.Domain.Constants;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Maps PowerBase field metadata onto the engine's <see cref="FormulaType"/> system.
/// Lives in the host (not the Domain-pure engine) because it knows about TypeCodes
/// and field settings.
/// </summary>
internal static class FormulaTypeMap
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The <see cref="FormulaType"/> a field's values present to a formula, or null
    /// when the field type cannot be referenced in a scalar formula (File, Address,
    /// ranges). For Formula fields, the declared result type is used.
    /// </summary>
    public static FormulaType? FieldType(string typeCode, string? settingsJson) => typeCode switch
    {
        "Text" or "TextMultiLine" or "RichText" or "SingleSelect" or "MultiSelect"
            or "Email" or "Phone" or "Url" => FormulaType.Text,
        "Time" => FormulaType.Time,
        "Number" or "Currency" or "Percent" or "Rating" => FormulaType.Number,
        "Date" => FormulaType.Date,
        "DateTime" => FormulaType.DateTime,
        "Duration" => FormulaType.Duration,
        "Boolean" => FormulaType.Bool,
        "User" => FormulaType.User,
        "MultiUser" => FormulaType.UserList,
        "Formula" => ResultType(ParseSettings(settingsJson)?.ResultType),
        "Lookup" => LookupResultType(settingsJson),
        "Summary" => SummaryResultType(settingsJson),
        _ when typeCode.StartsWith("Formula_", StringComparison.Ordinal) => FormulaVariantResultType(typeCode),
        _ => null,
    };

    /// <summary>The FormulaType a Formula_{X} variant field presents when referenced from
    /// another formula — derived from the TypeCode suffix (no ResultType stored per-variant,
    /// unlike the generic "Formula" code).</summary>
    private static FormulaType FormulaVariantResultType(string typeCode) => typeCode switch
    {
        "Formula_Text" or "Formula_Phone" or "Formula_Email" or "Formula_RichText" or "Formula_Url" => FormulaType.Text,
        "Formula_Number" => FormulaType.Number,
        "Formula_Date" => FormulaType.Date,
        "Formula_DateTime" => FormulaType.DateTime,
        "Formula_Time" => FormulaType.Time,
        "Formula_Duration" => FormulaType.Duration,
        "Formula_Bool" => FormulaType.Bool,
        "Formula_User" => FormulaType.User,
        _ => FormulaType.Text,
    };

    /// <summary>A Lookup presents the FormulaType of the parent field it pulls down (captured at creation).</summary>
    private static FormulaType? LookupResultType(string? settingsJson)
    {
        var s = ParseLookupSettings(settingsJson);
        return string.IsNullOrWhiteSpace(s?.SourceTypeCode) ? FormulaType.Text : FieldType(s!.SourceTypeCode!, null);
    }

    /// <summary>Count/Sum/Avg present as Number; Min/Max present as the aggregated field's type.</summary>
    private static FormulaType? SummaryResultType(string? settingsJson)
    {
        var s = ParseSummarySettings(settingsJson);
        if (s is null) return FormulaType.Number;
        return s.Function switch
        {
            "Exists" => FormulaType.Bool,
            "Min" or "Max" when !string.IsNullOrWhiteSpace(s.TargetTypeCode) => FieldType(s.TargetTypeCode!, null),
            _ => FormulaType.Number,
        };
    }

    /// <summary>Maps a <see cref="FormulaSettings.ResultType"/> string to a FormulaType (defaults to Text).</summary>
    public static FormulaType ResultType(string? resultType) =>
        Enum.TryParse<FormulaType>(resultType, ignoreCase: true, out var t) ? t : FormulaType.Text;

    /// <summary>Parses an optional expected-result-type string, or null when absent/unrecognised.</summary>
    public static FormulaType? ParseExpected(string? resultType) =>
        string.IsNullOrWhiteSpace(resultType) ? null
        : Enum.TryParse<FormulaType>(resultType, ignoreCase: true, out var t) ? t : null;

    public static FormulaSettings? ParseSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<FormulaSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static LookupSettings? ParseLookupSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<LookupSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static SummarySettings? ParseSummarySettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<SummarySettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static ReportLinkSettings? ParseReportLinkSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<ReportLinkSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static ReferenceSettings? ParseReferenceSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<ReferenceSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>The template of a Formula_Url field, or null when absent/blank.</summary>
    public static string? FormulaUrlTemplate(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<FormulaUrlSettings>(settingsJson, JsonOpts);
            return string.IsNullOrWhiteSpace(s?.Template) ? null : s!.Template;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A field whose value is computed at read time and has no physical column: a Formula
    /// (generic or Formula_{X} variant, including Formula_Url), Lookup or Summary field. Used
    /// for the in-memory filter/sort split (these fields cannot be filtered/sorted in SQL).
    /// </summary>
    public static bool IsComputedField(string typeCode, string? settingsJson) =>
        PhysicalNaming.IsComputedTypeCode(typeCode);

    /// <summary>
    /// A field computed specifically by the formula engine: the generic "Formula" code, or any
    /// Formula_{X} variant (including Formula_Url). Excludes Lookup/Summary (computed by the
    /// relationship projector instead), so the formula projector never tries to compile them.
    /// </summary>
    public static bool IsFormulaComputed(string typeCode, string? settingsJson) =>
        typeCode is "Formula" || PhysicalNaming.IsFormulaVariantTypeCode(typeCode);

    /// <summary>
    /// Returns the expression text and target FormulaType for any computed Formula-family
    /// field — the generic "Formula" code (reads Settings.ResultType, legacy), every
    /// Formula_{X} variant (X derived directly from the TypeCode suffix — no ResultType
    /// stored per-variant), including Formula_Url (always Text, template read from
    /// FormulaUrlSettings.Template instead of a generic Expression key). Null when there's no
    /// usable expression yet (blank/absent Settings). This is the single dispatch point
    /// FormulaProjector compiles against.
    /// </summary>
    public static (string Expression, FormulaType Type)? ExpressionAndType(string typeCode, string? settingsJson) => typeCode switch
    {
        "Formula" => GenericFormulaExpressionAndType(settingsJson),
        "Formula_Url" => FormulaUrlTemplate(settingsJson) is { } tpl ? (tpl, FormulaType.Text) : null,
        "Formula_Text" => FromExpr<FormulaTextSettings>(settingsJson, s => s.Expression, FormulaType.Text),
        "Formula_Number" => FromExpr<FormulaNumericSettings>(settingsJson, s => s.Expression, FormulaType.Number),
        "Formula_Date" => FromExpr<FormulaDateSettings>(settingsJson, s => s.Expression, FormulaType.Date),
        "Formula_DateTime" => FromExpr<FormulaDateSettings>(settingsJson, s => s.Expression, FormulaType.DateTime),
        "Formula_Time" => FromExpr<FormulaTimeSettings>(settingsJson, s => s.Expression, FormulaType.Time),
        "Formula_Duration" => FromExpr<FormulaDurationSettings>(settingsJson, s => s.Expression, FormulaType.Duration),
        "Formula_Bool" => FromExpr<FormulaBooleanSettings>(settingsJson, s => s.Expression, FormulaType.Bool),
        "Formula_Phone" => FromExpr<FormulaPhoneSettings>(settingsJson, s => s.Expression, FormulaType.Text),
        "Formula_Email" => FromExpr<FormulaEmailSettings>(settingsJson, s => s.Expression, FormulaType.Text),
        "Formula_User" => FromExpr<FormulaUserSettings>(settingsJson, s => s.Expression, FormulaType.User),
        "Formula_RichText" => FromExpr<FormulaRichTextSettings>(settingsJson, s => s.Expression, FormulaType.Text),
        _ => null,
    };

    private static (string Expression, FormulaType Type)? FromExpr<T>(string? settingsJson, Func<T, string?> exprSelector, FormulaType type) where T : class
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<T>(settingsJson, JsonOpts);
            var expr = s is null ? null : exprSelector(s);
            return string.IsNullOrWhiteSpace(expr) ? null : (expr!, type);
        }
        catch (JsonException) { return null; }
    }

    // Used only by the legacy "Formula" case above, where the target type depends on a second
    // settings field (ResultType) rather than being fixed per TypeCode.
    private static (string Expression, FormulaType Type)? GenericFormulaExpressionAndType(string? settingsJson)
    {
        var s = ParseSettings(settingsJson);
        return string.IsNullOrWhiteSpace(s?.Expression) ? null : (s!.Expression!, ResultType(s.ResultType));
    }
}
