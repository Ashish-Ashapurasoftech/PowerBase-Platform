namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// Maps Quickbase QBL field type identifiers (the <c>QB::Field::*</c> namespaced types) to
/// PowerBase <see cref="PowerBase.Domain.Enums.FieldTypeCode"/> codes. Consumed by the QBL→PBL
/// converter (Phase 3). Defined now so the Phase 1 PBL field-type surface is deliberately
/// scoped to what a QBL import can eventually populate.
///
/// Types not present in this map are not silently dropped by the importer — they are
/// surfaced as "unsupported, flagged for manual review" in the import preview/report.
/// </summary>
public static class QblFieldTypeMap
{
    /// <summary>QBL type identifier → PowerBase FieldTypeCode. Scalar types only (Phase 1 scope).</summary>
    public static readonly IReadOnlyDictionary<string, string> ScalarTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["QB::Field::Text"] = "Text",
        ["QB::Field::Text::MultiLine"] = "TextMultiLine",
        ["QB::Field::Text::RichText"] = "RichText",
        ["QB::Field::Text::Multichoice"] = "SingleSelect",
        ["QB::Field::Text::Multiselect"] = "MultiSelect",
        ["QB::Field::Numeric"] = "Number",
        ["QB::Field::Numeric::Currency"] = "Currency",
        ["QB::Field::Numeric::Percent"] = "Percent",
        ["QB::Field::Numeric::Rating"] = "Rating",
        ["QB::Field::Date"] = "Date",
        ["QB::Field::DateTime"] = "DateTime",
        ["QB::Field::TimeOfDay"] = "Time",
        ["QB::Field::Duration"] = "Duration",
        ["QB::Field::Checkbox"] = "Boolean",
        ["QB::Field::EmailAddress"] = "Email",
        ["QB::Field::PhoneNumber"] = "Phone",
        ["QB::Field::URL"] = "Url",
        ["QB::Field::FileAttachment"] = "File",
        ["QB::Field::Address"] = "Address",
    };

    /// <summary>QBL types PBL supports but only from Phase 2+ (not created by Phase 1 import).</summary>
    public static readonly IReadOnlyDictionary<string, string> DeferredTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["QB::Field::User"] = "User",
        ["QB::Field::User::List"] = "MultiUser",
        ["QB::Field::Formula"] = "Formula",
        ["QB::Field::URL::Formula"] = "Formula",
        ["QB::Field::Reference"] = "Reference",
        ["QB::Field::Lookup"] = "Lookup",
        ["QB::Field::Summary"] = "Summary",
        ["QB::Field::ReportLink"] = "ReportLink",
    };

    /// <summary>QBL types with no PowerBase equivalent as of this writing (e.g. vCard/iCalendar
    /// per Quickbase's own v0.12 export notes). Always flagged, never silently dropped.</summary>
    public static readonly IReadOnlyCollection<string> Unsupported = new[]
    {
        "QB::Field::VCard",
        "QB::Field::ICalendar",
    };

    public static bool TryMapScalar(string qblType, out string powerBaseTypeCode) =>
        ScalarTypes.TryGetValue(qblType, out powerBaseTypeCode!);
}
