using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Qbl;

/// <summary>What a QBL field node carries beyond its type: the per-type settings blob and the common field flags.</summary>
public sealed class QblFieldMapping
{
    /// <summary>JSON for <c>AppField.Settings</c> (display/behaviour options), or null when the source set none.</summary>
    public string? SettingsJson { get; init; }

    public string? DefaultValue { get; init; }
    public string? Description { get; init; }
    public string? HelpText { get; init; }
    public bool IsRequired { get; init; }
    public bool? IsUnique { get; init; }
    public bool? IsAuditable { get; init; }

    /// <summary>Only ever false: a source "not searchable" / "not in reports" is honoured; "true" leaves PowerBase's own per-type default.</summary>
    public bool? IsSearchable { get; init; }
    public bool? IsReportable { get; init; }

    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// Maps a QBL field's properties to PowerBase field settings and flags (see QBL_TO_PBL_MAPPING_SPEC.md §3–4). Pure —
/// no I/O. Values with no PowerBase destination are reported in <see cref="QblFieldMapping.Notes"/>, never silently
/// invented. Explicit field-level values are written as-is; an absent one stays null so the app-level formatting
/// (currency symbol/position, separators, decimals) applies as the fallback.
/// </summary>
public static class QblFieldMapper
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int MIN_COLUMN_WIDTH = 20;
    private const int MAX_COLUMN_WIDTH = 2000;

    /// <param name="pbTypeCode">PowerBase type: a stored type ("Text", "Currency"…) or a <c>Formula_*</c> variant.</param>
    public static QblFieldMapping Map(QblResourceNode node, string pbTypeCode)
    {
        var notes = new List<string>();
        var s = new Dictionary<string, object?>(StringComparer.Ordinal);

        var isFormula = pbTypeCode.StartsWith("Formula_", StringComparison.Ordinal);
        var baseType = isFormula ? pbTypeCode["Formula_".Length..] : pbTypeCode;

        // ---- common display flags (every settings class carries them except RichText: column width only) ----
        if (baseType != "RichText" && Bool(node, "IsBold") == true) s["displayBold"] = true;
        if (baseType != "RichText" && Bool(node, "DisplayWithoutWrapping") == true) s["noWrap"] = true;
        var width = Int(node, "DefaultColumnWidth");
        if (width is >= MIN_COLUMN_WIDTH and <= MAX_COLUMN_WIDTH) s["columnWidth"] = width;
        // QBL "Width" is an input size in characters, not a column width in pixels — not carried.

        switch (baseType)
        {
            case "Text":
            case "TextMultiLine":
                if (Int(node, "MaxLength") is > 0 and var maxLength)
                    s["validation"] = new Dictionary<string, object?> { ["maxLength"] = maxLength };
                break;

            case "RichText":
                if (Int(node, "MaxLength") is > 0 and var richMax) s["maxLength"] = richMax;
                break;

            case "SingleSelect":
            case "MultiSelect":
                var options = Choices(node);
                if (options.Count > 0) s["options"] = options;
                if (baseType == "SingleSelect" && Int(node, "MaxLength") is > 0 and var selectMax) s["maxLength"] = selectMax;
                break;

            case "Number":
            case "Currency":
            case "Percent":
            case "Rating":
                MapNumeric(node, s, isFormula, baseType, notes);
                break;

            case "Date":
            case "DateTime":
                if (Bool(node, "ShowMonthAsAlpha") == true) s["showMonthName"] = true;
                if (Bool(node, "HideCurrentYear") == true) s["hideYearIfCurrent"] = true;
                if (Bool(node, "ShowDayOfWeek") == true) s["showDayOfWeek"] = true;
                if (baseType == "DateTime" && Bool(node, "ShowTime") is { } showTime) s["showTime"] = showTime;
                break;

            case "Time":
                if (Bool(node, "Use24HourClock") == true) s["use24Hour"] = true;
                s["format"] = Str(node, "DisplayAs") == "HH:MM:SS" ? "HHMMSS" : "HHMM";
                break;

            case "Duration":
                if (DurationDisplay(Str(node, "DisplayUnits")) is { } display) s["display"] = display;
                if (!isFormula)
                {
                    if (Int(node, "DecimalPlaces") is >= 0 and <= 6 and var durDecimals) s["decimals"] = durDecimals;
                    if (Bool(node, "TreatBlankAsZero") == true) s["treatBlankAsZero"] = true;
                }
                if (Bool(node, "DoesTotal") == true) s["showTotalInReports"] = true;
                if (Bool(node, "DoesAverage") == true) s["showAverageInReports"] = true;
                break;

            case "Email":
                if (!string.IsNullOrWhiteSpace(Str(node, "LinkText"))) s["linkText"] = Str(node, "LinkText");
                switch (Str(node, "DisplayAs"))
                {
                    case "FullEmailAddress": s["showFullAddress"] = true; break;
                    case "OmitDomain": s["showBeforeAt"] = true; break;
                    case "OmitAfterUnderscore": s["showBeforeUnderscore"] = true; break;
                }
                break;

            case "Url":
                MapUrl(node, s);
                break;

            case "File":
                if (!string.IsNullOrWhiteSpace(Str(node, "LinkText"))) s["linkText"] = Str(node, "LinkText");
                if (Bool(node, "ShowAsImages") == true) s["showImagePreview"] = true;
                if (Int(node, "RevisionsToKeep") is { } revisions)
                {
                    if (revisions <= 0) s["keepAllRevisions"] = true;
                    else if (revisions <= 100) s["revisionLimit"] = revisions;
                }
                if (Bool(node, "AllowPromoteVersion") == true) s["allowOlderVersionsAsCurrent"] = true;
                break;

            case "Boolean":
            case "Bool":
                // Quickbase "show as checkmark" is the opposite of PowerBase "show Yes/No".
                if (Bool(node, "ShowAsCheckmark") == false) s["showYesNo"] = true;
                if (Bool(node, "DoesTotal") == true) s["showTotalsRow"] = true;
                break;

            case "User":
                switch (Str(node, "DisplayAs"))
                {
                    case "FirstNameLastName": s["displayAs"] = "FullName"; break;
                    case "LastNameFirstName": s["displayAs"] = "LastFirst"; break;
                    case "UserName": s["displayAs"] = "UserName"; break;
                }
                if (Node(node, "UserSet") is not null || ListOf(node, "Choices").Count > 0)
                    notes.Add("A restricted user list cannot be carried (users are resolved per tenant); the field allows the default user set.");
                break;
        }

        // Settings that only make sense on the formula variants are copied above; the expression itself is added by the importer.

        string? defaultValue = isFormula ? null : DefaultValue(node, baseType, notes);

        return new QblFieldMapping
        {
            SettingsJson = s.Count == 0 ? null : JsonSerializer.Serialize(s, Json),
            DefaultValue = defaultValue,
            Description = NullIfBlank(Str(node, "Comments")),
            HelpText = NullIfBlank(Str(node, "HelpText")),
            IsRequired = Bool(node, "IsRequired") == true,
            IsUnique = Bool(node, "IsUnique") == true ? true : null,
            IsAuditable = Bool(node, "IsAuditable") == true ? true : null,
            IsSearchable = Bool(node, "IsSearchable") == false ? false : null,
            IsReportable = Bool(node, "AllowedInReports") == false ? false : null,
            Notes = notes,
        };
    }

    // ---- numeric ----

    private static void MapNumeric(QblResourceNode node, Dictionary<string, object?> s, bool isFormula, string baseType, List<string> notes)
    {
        // "FloatingPoint" = as many decimals as the value has → no fixed decimals.
        if (Int(node, "DecimalPlaces") is >= 0 and <= 10 and var decimals) s["decimals"] = decimals;

        var style = Str(Node(node, "NumberFormat"), "SeparatorStyle");
        if (style == "CommaGroupingPeriodDecimal") s["separator"] = ",";
        else if (style == "PeriodGroupingCommaDecimal") s["separator"] = ".";

        if (!string.IsNullOrWhiteSpace(Str(node, "Units"))) s["unitsDescription"] = Str(node, "Units");
        if (Bool(node, "DoesTotal") == true) s["showTotalInReports"] = true;
        if (Bool(node, "DoesAverage") == true) s["showAverageInReports"] = true;
        if (!isFormula && Bool(node, "TreatBlankAsZero") == true) s["treatBlankAsZero"] = true;

        if (baseType == "Currency" || (isFormula && !string.IsNullOrWhiteSpace(Str(node, "CurrencySymbol"))))
        {
            if (!string.IsNullOrWhiteSpace(Str(node, "CurrencySymbol")) && Str(node, "CurrencySymbol")!.Length <= 10)
                s["symbol"] = Str(node, "CurrencySymbol");

            switch (Str(node, "CurrencySymbolPosition"))
            {
                case "Before": s["position"] = "before"; break;
                case "After": s["position"] = "after"; break;
                case "Between":
                    // Stored currency fields only allow before/after; formulas allow "betweenSignAndNumber".
                    if (isFormula) s["position"] = "betweenSignAndNumber";
                    break; // otherwise inherit the app-level position
            }
        }

        if (baseType == "Rating" && Int(node, "Max") is >= 1 and <= 20 and var max) s["max"] = max;
    }

    private static void MapUrl(QblResourceNode node, Dictionary<string, object?> s)
    {
        if (!string.IsNullOrWhiteSpace(Str(node, "LinkText"))) s["linkText"] = Str(node, "LinkText");
        if (Bool(node, "DisplayAsButton") == true) s["displayAsButton"] = true;
        if (Bool(node, "EmbedAsIframe") == true) s["embedAsIframe"] = true;
        switch (Str(node, "Target"))
        {
            case "SamePage": s["openTarget"] = "SamePage"; break;
            case "NewWindow": s["openTarget"] = "NewWindow"; break;
            case "Popup": s["openTarget"] = "Popup"; break;
        }
    }

    private static string? DurationDisplay(string? units) => units switch
    {
        "Smart Units" => "Smart",
        "HH:MM" => "HHMM",
        "HH:MM:SS" => "HHMMSS",
        ":MM" => "MM",
        ":MM:SS" => "MMSS",
        "Weeks" => "Weeks",
        "Days" => "Days",
        "Hours" => "Hours",
        "Minutes" => "Minutes",
        "Seconds" => "Seconds",
        _ => null,
    };

    // ---- default value ----

    private static string? DefaultValue(QblResourceNode node, string baseType, List<string> notes)
    {
        var raw = Str(node, "DefaultValue");

        switch (baseType)
        {
            case "Boolean":
                return raw switch { "Checked" => "true", "Unchecked" => "false", _ => null };

            case "Date":
                return Bool(node, "DefaultIsToday") == true ? "true" : null;

            case "DateTime":
                return Bool(node, "DefaultIsNow") == true ? "true" : null;

            case "User":
                if (string.Equals(raw, "CurrentUser", StringComparison.OrdinalIgnoreCase))
                    return "{\"mode\":\"CurrentUser\"}";
                if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
                    notes.Add($"User default '{raw}' cannot be carried (users are resolved per tenant).");
                return null;

            case "MultiUser":
                return string.Equals(raw, "CurrentUser", StringComparison.OrdinalIgnoreCase) ? "{\"mode\":\"CurrentUser\"}" : null;

            default:
                return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    // ---- choices ----

    private static List<string> Choices(QblResourceNode node)
    {
        var list = ListOf(Node(node, "InputOptions"), "ListChoices");
        if (list.Count == 0) list = ListOf(node, "Choices");

        var choices = list.Select(x => x?.ToString()?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (string.Equals(Str(Node(node, "InputOptions"), "ChoiceOrder"), "Alphabetical", StringComparison.OrdinalIgnoreCase))
            choices = choices.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        return choices;
    }

    // ---- property readers (nodes and plain nested dictionaries) ----

    private static IReadOnlyDictionary<string, object?>? Node(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) ? v as IReadOnlyDictionary<string, object?> ?? (v as IDictionary<string, object?>)?.ToDictionary(k => k.Key, k => k.Value) : null;

    private static string? Str(QblResourceNode node, string name) => node.Properties.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static string? Str(IReadOnlyDictionary<string, object?>? d, string name) =>
        d is not null && d.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static bool? Bool(QblResourceNode node, string name) => ParseBool(node.Properties.TryGetValue(name, out var v) ? v : null);

    private static bool? ParseBool(object? v) => v switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };

    private static int? Int(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) && v is not null && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static List<object?> ListOf(QblResourceNode node, string name) =>
        node.Properties.TryGetValue(name, out var v) && v is IEnumerable<object?> l && v is not string ? l.ToList() : [];

    private static List<object?> ListOf(IReadOnlyDictionary<string, object?>? d, string name) =>
        d is not null && d.TryGetValue(name, out var v) && v is IEnumerable<object?> l && v is not string ? l.ToList() : [];

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
