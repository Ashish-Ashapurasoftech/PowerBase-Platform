using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// Builds Quickbase's field description (getFields item / API_GetFieldProperties) from one of our fields.
/// Values come from AppField columns and its Settings JSON; every key Quickbase documents is always written,
/// null where we have no equivalent.
/// </summary>
public static class QbFieldPropertiesBuilder
{
    /// <summary>The 73 property keys of getFields[].properties, in the order of the official OpenAPI schema.</summary>
    public static readonly string[] PROPERTY_KEYS =
    {
        "comments", "doesTotal", "autoSave", "defaultValueLuid", "useI18NFormat", "maxVersions", "carryChoices", "format",
        "maxLength", "linkText", "parentFieldId", "displayTimezone", "summaryTargetFieldId", "allowNewChoices",
        "masterChoiceFieldId", "defaultToday", "units", "lookupTargetFieldId", "summaryFunction", "sourceFieldId",
        "masterTableTag", "doesAverage", "formula", "decimalPlaces", "defaultCountryCode", "seeVersions", "displayMonth",
        "numLines", "displayEmail", "defaultKind", "coverText", "currencySymbol", "masterChoiceTableId", "targetFieldId",
        "displayUser", "blankIsZero", "exact", "startField", "defaultDomain", "defaultValue", "choicesLuid", "abbreviate",
        "xmlTag", "targetTableName", "numberFormat", "appearsAs", "width", "currencyFormat", "foreignKey",
        "displayDayOfWeek", "summaryReferenceFieldId", "commaStart", "choices", "targetTableId", "displayRelative",
        "compositeFields", "displayCheckboxAsText", "versionMode", "displayTime", "durationField", "snapFieldId",
        "hours24", "sortAlpha", "sortAsGiven", "hasExtension", "workWeek", "useNewWindow", "postTempToken", "appendOnly",
        "displayAsLink", "allowHTML", "allowMentions", "lookupReferenceFieldId",
    };

    /// <param name="overrides">Field Id → type decided elsewhere (Reference fields take their parent key's type, see QbReferenceTypes).</param>
    public static QbFieldType TypeOf(AppField field, IReadOnlyDictionary<long, QbFieldType>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(field.Id, out var forced)) return forced;
        using var settings = Parse(field.Settings);
        var root = settings?.RootElement;
        string? result = null;
        if (field.TypeCode is "Lookup" or "Summary") result = Str(root, "ResultTypeCode");
        var mapped = QbFieldTypeMap.Map(field.TypeCode, result);
        // Built-in fields keep Quickbase's own types: Record ID# is "recordid" whatever our storage type is.
        if (field.IsSystem && field.Fid == QbIds.RECORD_ID_FID) return new QbFieldType("recordid", "", "int64");
        if (field.TypeCode is "Number" or "Formula_Number")
        {
            var displayAs = Str(root, "DisplayAs");
            var numericType = displayAs switch { "Currency" => "currency", "Percent" => "percent", "Rating" => "rating", _ => null };
            if (numericType != null) mapped = mapped with { FieldType = numericType };
        }
        return mapped;
    }

    public static Dictionary<string, object?> BuildProperties(AppField field)
    {
        var p = PROPERTY_KEYS.ToDictionary(k => k, _ => (object?)null);
        using var settings = Parse(field.Settings);
        var s = settings?.RootElement;
        var type = field.TypeCode;

        p["comments"] = field.Description;
        p["doesTotal"] = Bool(s, "ShowTotalInReports");
        p["doesAverage"] = Bool(s, "ShowAverageInReports");
        p["maxLength"] = Int(s, "MaxLength") ?? Int(Obj(s, "Validation"), "MaxLength") ?? field.MaxLength;
        p["linkText"] = Str(s, "LinkText");
        p["units"] = Str(s, "UnitsDescription");
        p["decimalPlaces"] = Int(s, "Decimals") ?? Int(s, "DecimalPlaces");
        p["formula"] = type.StartsWith("Formula", StringComparison.Ordinal) ? QbFormula(Str(s, "Expression") ?? Str(s, "Template")) : null;
        p["defaultValue"] = field.DefaultValue;
        p["blankIsZero"] = Bool(s, "TreatBlankAsZero");
        p["width"] = Int(s, "ColumnWidth");
        p["displayDayOfWeek"] = Bool(s, "ShowDayOfWeek");
        p["displayTime"] = Bool(s, "ShowTime");
        p["hours24"] = Bool(s, "Use24Hour");
        p["displayCheckboxAsText"] = Bool(s, "ShowYesNo");
        p["snapFieldId"] = field.SnapshotSourceFid;
        p["foreignKey"] = type == "Reference";
        p["allowHTML"] = type is "RichText" or "Formula_RichText";

        if (Arr(s, "Options") is { } options) p["choices"] = options;
        if (Obj(s, "ChoiceSource") is { } source)
        {
            p["masterChoiceFieldId"] = Int(source, "Fid");
            p["masterChoiceTableId"] = Str(source, "TableId");
        }
        if (Str(s, "DisplayAs") == "Currency" || type is "Currency" or "Formula_Currency")
        {
            p["currencySymbol"] = Str(s, "Symbol");
            p["currencyFormat"] = Str(s, "Position") switch { "before" => "left", "after" => "right", "between" => "middle", _ => null };
        }
        if (Str(s, "OpenTarget") is { } target) p["useNewWindow"] = target == "NewWindow";
        if (type == "File")
        {
            p["maxVersions"] = Int(s, "RevisionLimit");
            var keepAll = Bool(s, "KeepAllRevisions");
            p["seeVersions"] = keepAll;
            p["versionMode"] = keepAll switch { true => "keepallversions", false => "keeplastversions", null => null };
        }
        if (type == "Lookup")
        {
            p["lookupTargetFieldId"] = Int(s, "SourceFid");
            p["lookupReferenceFieldId"] = Int(s, "ReferenceFid");
        }
        if (type == "Summary")
        {
            p["summaryTargetFieldId"] = Int(s, "TargetFid");
            p["summaryReferenceFieldId"] = Int(s, "ReferenceFid");
            p["summaryFunction"] = SummaryFunction(Str(s, "Function"));
        }
        if (type is "Lookup" or "Summary" or "Reference")
            p["displayAsLink"] = Bool(s, "DisplayAsLinkToParent");
        return p;
    }

    /// <summary>Our stored formula references fields as <c>[fid_24|Account Manager]</c>; Quickbase shows <c>[Account Manager]</c>.</summary>
    public static string? QbFormula(string? formula) =>
        formula is null ? null : System.Text.RegularExpressions.Regex.Replace(formula, @"\[fid_\d+\|([^\]]+)\]", "[$1]");

    public static bool? NoWrap(AppField field) { using var d = Parse(field.Settings); return Bool(d?.RootElement, "NoWrap") ?? false; }
    public static bool? Bold(AppField field) { using var d = Parse(field.Settings); return Bool(d?.RootElement, "DisplayBold") ?? false; }

    /// <summary>Quickbase summaryFunction enum: AVG, SUM, MAX, MIN, STD-DEV, COUNT, COMBINED-TEXT, COMBINED-USER, DISTINCT-COUNT.</summary>
    public static string? SummaryFunction(string? function) => function?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "avg" or "average" => "AVG",
        "sum" or "total" => "SUM",
        "max" or "maximum" => "MAX",
        "min" or "minimum" => "MIN",
        "stddev" or "std-dev" or "standarddeviation" => "STD-DEV",
        "count" => "COUNT",
        "combinedtext" or "combined-text" => "COMBINED-TEXT",
        "combineduser" or "combined-user" => "COMBINED-USER",
        "distinctcount" or "distinct-count" or "countdistinct" => "DISTINCT-COUNT",
        var other => other.ToUpperInvariant(),
    };

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }

    private static JsonElement? Prop(JsonElement? obj, string name)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value;
        return null;
    }

    private static JsonElement? Obj(JsonElement? obj, string name) => Prop(obj, name) is { ValueKind: JsonValueKind.Object } v ? v : null;
    private static string? Str(JsonElement? obj, string name) => Prop(obj, name) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
    private static bool? Bool(JsonElement? obj, string name) => Prop(obj, name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static int? Int(JsonElement? obj, string name) => Prop(obj, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;
    private static List<string>? Arr(JsonElement? obj, string name) => Prop(obj, name) is { ValueKind: JsonValueKind.Array } v
        ? v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList()
        : null;
}
