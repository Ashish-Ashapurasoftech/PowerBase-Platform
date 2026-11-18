using System.Text.Json;
using System.Xml.Linq;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// The exact shape Quickbase gives a field — measured on a live realm (ysl.quickbase.com, 2026-11-05) for every field type:
/// which <c>properties</c> keys getFields returns for a type (and their defaults), and which child elements API_GetFieldProperties
/// writes, in Quickbase's order. Values we store (settings, help text, flags) override the Quickbase defaults.
/// </summary>
/// <summary>What a field inherits through a relationship: a summary's target field, a lookup's source field, a reference's parent table.</summary>
public sealed record QbFieldContext(AppField? Inherit = null, string? MasterTableId = null, string? MasterTableTag = null, int? MasterKeyFid = null);

public static class QbFieldShape
{
    /// <summary>Types Quickbase marks searchable by default (live realm): text-like, choices, dates, checkbox, e-mail, address, users.</summary>
    private static readonly string[] FIND_TYPES = { "text", "rich-text", "text-multiple-choice", "multitext", "date", "checkbox", "email", "address", "user", "multiuser" };

    public static bool IsReference(AppField f) => f.TypeCode == "Reference";
    private static readonly string[] NO_WRAP_TYPES = { "numeric", "currency", "percent", "rating", "date", "timestamp", "timeofday", "phone", "user", "recordid" };

    public static bool IsSystem(AppField f) => f.Fid is >= 1 and <= 5;

    /// <summary>Default "noWrap" when the field has no setting: Quickbase keeps numbers, dates, times, phones and users on one line.</summary>
    public static bool NoWrap(AppField f, QbFieldType t) => SettingBool(f, "NoWrap") ?? NO_WRAP_TYPES.Contains(t.FieldType);
    public static bool AppearsByDefault(AppField f) => !IsSystem(f) && !IsReference(f);

    /// <summary>Quickbase: summaries are never searchable; Record Owner / Last Modified By always are; others follow the field.</summary>
    public static bool FindEnabled(AppField f, QbFieldType t) =>
        t.Mode == "summary" || IsReference(f) ? false
        : f.Fid is QbIds.RECORD_OWNER_FID or QbIds.LAST_MODIFIED_BY_FID ? true
        : t.Mode is "formula" or "lookup" ? FIND_TYPES.Contains(t.FieldType)   // calculated fields: Quickbase's type default
        : f.IsSearchable;

    /// <summary>Calculated fields (formula, summary, lookup) are not audited in Quickbase.</summary>
    public static bool Audited(AppField f, QbFieldType t) => t.Mode is "" && !IsReference(f) && f.IsAuditable;
    public static bool DoesDataCopy(AppField f, QbFieldType t) =>
        !IsSystem(f) && t.Mode is "" && t.FieldType is not ("file" or "dblink" or "recordid");

    /// <summary>getFields "properties" for one field: the keys Quickbase returns for its type, with Quickbase's defaults.</summary>
    public static Dictionary<string, object?> Properties(AppField f, QbFieldType t, QbFieldContext? ctx = null)
    {
        var o = QbFieldPropertiesBuilder.BuildProperties(f);          // what our settings say
        var target = ctx?.Inherit is null ? null : QbFieldPropertiesBuilder.BuildProperties(ctx.Inherit);
        // A summary / lookup shows its numbers the way the field it totals / looks up does (decimals, currency symbol / position).
        object? Ours(string key) => o.TryGetValue(key, out var v) && v is not null ? v
            : target is not null && key is "decimalPlaces" or "currencySymbol" or "currencyFormat" && target.TryGetValue(key, out var tv) ? tv : null;
        var formula = t.Mode == "formula" ? (string?)Ours("formula") ?? "" : "";
        var p = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["allowNewChoices"] = t.Mode == "" && t.FieldType is "text-multiple-choice" or "multitext" or "user" or "multiuser",
            ["carryChoices"] = true,
            ["defaultValue"] = f.DefaultValue ?? (t.FieldType == "checkbox" && t.Mode is "" or "lookup" ? "0" : ""),
            ["foreignKey"] = f.TypeCode == "Reference",
            ["formula"] = formula,
            ["primaryKey"] = f.Fid == QbIds.RECORD_ID_FID,
            ["sortAsGiven"] = true,
        };
        if (IsReference(f))
        {
            // A reference field: Quickbase describes it as a whole number pointing at the parent table's key.
            p["blankIsZero"] = true; p["commaStart"] = 4; p["decimalPlaces"] = 0; p["doesAverage"] = false; p["doesTotal"] = false; p["numberFormat"] = 0;
            p["masterTableId"] = ctx?.MasterTableId; p["masterTableKeyFid"] = ctx?.MasterKeyFid ?? QbIds.RECORD_ID_FID; p["masterTableTag"] = ctx?.MasterTableTag;
            return new Dictionary<string, object?>(p);
        }
        if (t.Mode == "lookup") p["displayAsLink"] = (bool?)Ours("displayAsLink") ?? false;
        switch (t.FieldType)
        {
            case "phone":
                p["hasExtension"] = true;
                p["useI18NFormat"] = false;
                break;
            case "text" or "rich-text" or "text-multiple-choice" or "text-multi-line":
                p["allowHTML"] = t.FieldType == "rich-text";
                p["allowMentions"] = false;
                p["appendOnly"] = false;
                p["maxLength"] = Ours("maxLength") ?? 0;
                p["numLines"] = t.FieldType is "rich-text" or "text-multi-line" ? 6 : 1;
                if (t.FieldType is "rich-text" or "text-multi-line") p["width"] = 60;
                if (t.FieldType == "text-multiple-choice") p["choices"] = Ours("choices") ?? new List<string>();
                break;
            case "multitext":
                p["choices"] = Ours("choices") ?? new List<string>();
                break;
            case "numeric" or "currency" or "percent" or "rating" or "recordid":
                p["blankIsZero"] = (bool?)Ours("blankIsZero") ?? (t.FieldType != "rating" && t.Mode != "formula");
                p["commaStart"] = t.FieldType == "currency" ? 3 : 4;
                if (Ours("decimalPlaces") is { } dp) p["decimalPlaces"] = dp;
                else if (t.FieldType == "recordid") p["decimalPlaces"] = 0;
                else if (t.FieldType == "currency") p["decimalPlaces"] = 2;          // Quickbase's currency default
                else if (t.FieldType == "rating") p["decimalPlaces"] = 1;
                p["doesAverage"] = (bool?)Ours("doesAverage") ?? t.FieldType == "rating";
                p["doesTotal"] = (bool?)Ours("doesTotal") ?? t.FieldType is not ("rating" or "recordid");
                p["numberFormat"] = t.FieldType == "recordid" ? 0 : 8;
                if (Ours("units") is { } units) p["units"] = units;
                if (t.FieldType == "currency")
                {
                    p["currencyFormat"] = Ours("currencyFormat") ?? "middle";
                    p["currencySymbol"] = Ours("currencySymbol") ?? "$";
                }
                if (t.FieldType == "rating") p["choices"] = new List<string> { "0", "1", "2", "3", "4", "5" };
                break;
            case "date" or "timestamp":
                p["defaultToday"] = false;
                p["displayDayOfWeek"] = (bool?)Ours("displayDayOfWeek") ?? false;
                p["displayMonth"] = SettingBool(f, "ShowMonthName") == true ? "name" : "number";
                p["displayRelative"] = false;
                p["displayTime"] = (bool?)Ours("displayTime") ?? t.FieldType == "timestamp";
                p["displayTimezone"] = false;
                break;
            case "timeofday":
                p["displayZone"] = false;
                p["format"] = 0;
                p["hours24"] = (bool?)Ours("hours24") ?? false;
                break;
            case "duration":
                p["blankIsZero"] = (bool?)Ours("blankIsZero") ?? t.Mode != "formula";
                p["doesAverage"] = (bool?)Ours("doesAverage") ?? true;
                p["doesTotal"] = (bool?)Ours("doesTotal") ?? false;
                p["format"] = 6;
                break;
            case "checkbox":
                p["displayCheckboxAsText"] = (bool?)Ours("displayCheckboxAsText") ?? false;
                p["doesTotal"] = false;
                break;
            case "url":
                p["abbreviate"] = false;
                p["autoSave"] = false;
                p["openTargetIn"] = Ours("useNewWindow") is true ? "newWindow" : "sameWindow";
                if (t.Mode == "formula") p["postTempToken"] = false;
                break;
            case "file":
                p["maxVersions"] = Ours("maxVersions") ?? 3;
                p["seeVersions"] = Ours("seeVersions") ?? true;
                p["useNewWindow"] = true;
                p["versionMode"] = Ours("versionMode") ?? "keeplastversions";
                break;
            case "address":
                p["compositeFields"] = new List<object>();             // our address is one field, Quickbase splits it in six
                p["width"] = 60;
                break;
            case "user" or "multiuser":
                p["defaultKind"] = "none";
                p["displayUser"] = DisplayUser(f, t);
                break;
        }
        if (t.Mode == "summary")
        {
            p["fieldCaching"] = false;
            p["summaryFunction"] = Ours("summaryFunction");
            p["summaryReferenceFieldId"] = Ours("summaryReferenceFieldId");
            p["summaryTargetFieldId"] = Ours("summaryTargetFieldId") ?? 0;
        }
        if (t.Mode == "lookup")
        {
            p["lookupReferenceFieldId"] = Ours("lookupReferenceFieldId");
            p["lookupTargetFieldId"] = Ours("lookupTargetFieldId");
        }
        if (Ours("comments") is string c && c.Length > 0) p["comments"] = c;
        return new Dictionary<string, object?>(p);
    }

    /// <summary>API_GetFieldProperties: Quickbase's XML field_type (differs from REST for numbers, choices and users).</summary>
    public static string XmlFieldType(QbFieldType t) => t.FieldType switch
    {
        "numeric" => "float",
        "text-multiple-choice" or "rich-text" or "text-multi-line" => "text",
        "user" => "userid",
        "multiuser" => "multiuserid",
        var other => other,
    };

    /// <summary>The &lt;field&gt; element of API_GetFieldProperties, child elements in Quickbase's order for the type.</summary>
    public static XElement XmlField(AppField f, QbFieldType t, QbFieldContext? ctx = null)
    {
        var p = Properties(f, t, ctx);
        static string B(object? v) => v switch { true => "1", false => "0", null => "", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
        object? P(string k) => p.TryGetValue(k, out var v) ? v : null;
        var baseType = t.FieldType == "recordid" ? "int32" : IsReference(f) ? "float" : t.BaseType;
        var el = new XElement("field", new XAttribute("id", f.Fid!.Value), new XAttribute("field_type", XmlFieldType(t)), new XAttribute("base_type", baseType));
        var xmlMode = t.FieldType == "recordid" ? "virtual" : t.Mode switch { "formula" => "virtual", "" => null, var m => m };
        if (xmlMode is not null) el.Add(new XAttribute("mode", xmlMode));
        el.Add(new XElement("label", string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label),
            new XElement("nowrap", B(NoWrap(f, t))),
            new XElement("bold", B(SettingBool(f, "DisplayBold") ?? false)),
            new XElement("required", B(f.IsRequired)),
            new XElement("appears_by_default", B(AppearsByDefault(f))),
            new XElement("find_enabled", B(FindEnabled(f, t))),
            new XElement("allow_new_choices", B(P("allowNewChoices"))),
            new XElement("sort_as_given", B(P("sortAsGiven"))));
        if (P("choices") is IEnumerable<string> choices && t.FieldType is "text-multiple-choice" or "multitext" or "rating")
            el.Add(new XElement("choices", choices.Select(c => new XElement("choice", c))));
        if (t.FieldType is "address" or "rich-text" or "text-multi-line") el.Add(new XElement("width", B(P("width") ?? 60)));
        if (t.Mode == "formula") el.Add(new XElement("formula", P("formula") as string ?? ""));
        el.Add(new XElement("carrychoices", B(P("carryChoices"))),
            new XElement("foreignkey", "0"),                       // Quickbase XML: 0 even for a reference field
            new XElement("unique", B(f.IsUnique || f.Fid == QbIds.RECORD_ID_FID)),
            new XElement("doesdatacopy", B(DoesDataCopy(f, t))),
            new XElement("fieldhelp", f.HelpText ?? ""));
        if (t.Mode == "lookup")
            el.Add(new XElement("lutfid", B(P("lookupTargetFieldId"))), new XElement("lusfid", B(P("lookupReferenceFieldId"))));
        if (IsReference(f)) el.Add(new XElement("mastag", ctx?.MasterTableTag ?? ""));
        if (t.Mode == "summary")
        {
            el.Add(new XElement("summaryReferenceFid", B(P("summaryReferenceFieldId"))),
                new XElement("summaryTargetFid", B(P("summaryTargetFieldId"))),
                new XElement("summaryFunction", XmlSummaryFunction(P("summaryFunction") as string)));
        }
        el.Add(new XElement("audited", B(Audited(f, t))));
        switch (t.FieldType)
        {
            case "text" or "text-multiple-choice" or "rich-text" or "text-multi-line" or "phone":
                el.Add(new XElement("num_lines", B(P("numLines") ?? 1)), new XElement("maxlength", B(P("maxLength") ?? 0)),
                    new XElement("append_only", "0"), new XElement("allowHTML", B(P("allowHTML") ?? false)), new XElement("allowMentions", "0"));
                if (t.FieldType == "phone") el.Add(new XElement("has_extension", "1"), new XElement("use_i18n_format", "0"));
                break;
            case "numeric" or "currency" or "percent" or "rating" or "recordid":
                if (P("units") is { } u) el.Add(new XElement("units", B(u)));
                if (P("decimalPlaces") is { } dp) el.Add(new XElement("decimal_places", B(dp)));
                el.Add(new XElement("comma_start", B(P("commaStart"))), new XElement("numberfmt", B(P("numberFormat"))),
                    new XElement("does_average", B(P("doesAverage"))), new XElement("does_total", B(P("doesTotal"))),
                    new XElement("blank_is_zero", B(P("blankIsZero"))));
                if (t.FieldType == "currency")
                    el.Add(new XElement("currency_symbol", B(P("currencySymbol"))),
                        new XElement("currency_format", (P("currencyFormat") as string) switch { "left" => "0", "right" => "2", _ => "1" }));
                break;
            case "date" or "timestamp":
                el.Add(new XElement("display_time", B(P("displayTime"))), new XElement("display_relative", "0"),
                    new XElement("display_month", B(P("displayMonth"))), new XElement("default_today", "0"),
                    new XElement("display_dow", B(P("displayDayOfWeek"))), new XElement("display_zone", "0"));
                break;
            case "timeofday":
                el.Add(new XElement("format", "0"), new XElement("hours24", B(P("hours24"))), new XElement("display_zone", "0"));
                break;
            case "duration":
                el.Add(new XElement("format", "6"), new XElement("does_average", B(P("doesAverage"))),
                    new XElement("does_total", B(P("doesTotal"))), new XElement("blank_is_zero", B(P("blankIsZero"))));
                break;
            case "checkbox":
                el.Add(new XElement("display_graphic", B(!(P("displayCheckboxAsText") is true))), new XElement("does_total", "0"));
                break;
            case "url":
                el.Add(new XElement("abbreviate", "0"), new XElement("use_new_window", B(P("openTargetIn") as string == "newWindow")),
                    new XElement("auto_save", "0"), new XElement("embed_in_forms", "0"));
                if (t.Mode == "formula") el.Add(new XElement("postTempToken", "0"));
                break;
            case "file":
                el.Add(new XElement("max_versions", B(P("maxVersions"))), new XElement("see_versions", B(P("seeVersions"))),
                    new XElement("use_new_window", "1"));
                break;
            case "address":
                el.Add(new XElement("compositeFields"));
                break;
            case "user" or "multiuser":
                el.Add(new XElement("display_user", B(P("displayUser"))), new XElement("default_kind", "none"));
                break;
        }
        return el;
    }

    /// <summary>Quickbase XML summary function names (REST SUM → XML Total).</summary>
    public static string XmlSummaryFunction(string? rest) => rest switch
    {
        "SUM" => "Total", "COUNT" => "Count", "AVG" => "Average", "MAX" => "Maximum", "MIN" => "Minimum",
        "STD-DEV" => "Std Dev", "DISTINCT-COUNT" => "Distinct Count", "COMBINED-TEXT" => "Combined Text", "COMBINED-USER" => "Combined User",
        null => "", var other => other,
    };

    private static string DisplayUser(AppField f, QbFieldType t)
    {
        var s = SettingString(f, "DisplayAs");
        return s switch
        {
            "FullName" => "fullname",
            "LastFirst" => "fullnamelf",
            "UserName" or "Email" => "username",
            _ => t.FieldType == "multiuser" ? "fullname" : "fullnamelf",
        };
    }

    private static bool? SettingBool(AppField f, string name)
    {
        if (string.IsNullOrWhiteSpace(f.Settings)) return null;
        try
        {
            using var d = JsonDocument.Parse(f.Settings);
            foreach (var p in d.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return p.Value.GetBoolean();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? SettingString(AppField f, string name)
    {
        if (string.IsNullOrWhiteSpace(f.Settings)) return null;
        try
        {
            using var d = JsonDocument.Parse(f.Settings);
            foreach (var p in d.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
