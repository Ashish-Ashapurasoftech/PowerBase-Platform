namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Maps Quickbase QBL field type identifiers to PowerBase <see cref="PowerBase.Domain.Enums.FieldTypeCode"/>
/// codes. Identifiers here were verified against a real 55,844-line QBL v0.12 export, not
/// guessed from documentation — the docs (an AI summary of help.quickbase.com) turned out to
/// disagree with the real file on several names (e.g. real exports use flat
/// <c>QB::Field::Currency</c>, not a nested <c>QB::Field::Numeric::Currency</c>; real
/// multi-line/multi-select text types are <c>TextMultiline</c>/<c>MultiselectText</c>, not
/// <c>Text::MultiLine</c>/<c>Text::Multiselect</c>). Treat any future QBL sample as the source
/// of truth over documentation if the two disagree again.
///
/// Types not present in <see cref="ScalarTypes"/>/<see cref="FormulaResultTypes"/> are never
/// silently dropped by the importer — they are surfaced as "unsupported, flagged" in the
/// import preview/report.
/// </summary>
public static class QblFieldTypeMap
{
    /// <summary>QBL scalar field type identifier → PowerBase FieldTypeCode.</summary>
    public static readonly IReadOnlyDictionary<string, string> ScalarTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["QB::Field::Text"] = "Text",
        ["QB::Field::TextMultiline"] = "TextMultiLine",
        ["QB::Field::RichText"] = "RichText",
        ["QB::Field::TextMultipleChoice"] = "SingleSelect",
        ["QB::Field::MultiselectText"] = "MultiSelect",
        ["QB::Field::Number"] = "Number",
        ["QB::Field::Currency"] = "Currency",
        ["QB::Field::Percent"] = "Percent",
        ["QB::Field::Rating"] = "Rating",
        ["QB::Field::Date"] = "Date",
        ["QB::Field::DateTime"] = "DateTime",
        ["QB::Field::TimeOfDay"] = "Time",
        ["QB::Field::Duration"] = "Duration",
        ["QB::Field::Checkbox"] = "Boolean",
        ["QB::Field::Email"] = "Email",
        ["QB::Field::PhoneNumber"] = "Phone",
        ["QB::Field::URL"] = "Url",
        ["QB::Field::FileAttachment"] = "File",
        ["QB::Field::Address"] = "Address",
        ["QB::Field::User"] = "User",
        ["QB::Field::ListUser"] = "MultiUser",
    };

    /// <summary>QBL formula field type identifier → PowerBase FormulaResultTypes value (Text,
    /// Number, Date, DateTime, Duration, Bool, User). Real exports carry a genuine naming
    /// inconsistency: every other formula type is <c>&lt;BaseType&gt;::Formula</c>, but the
    /// numeric one is <c>QB::Field::Numeric::Formula</c>, not <c>Number::Formula</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> FormulaResultTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["QB::Field::Text::Formula"] = "Text",
        ["QB::Field::URL::Formula"] = "Text",
        ["QB::Field::RichText::Formula"] = "Text",
        ["QB::Field::Email::Formula"] = "Text",
        ["QB::Field::PhoneNumber::Formula"] = "Text",
        ["QB::Field::Checkbox::Formula"] = "Bool",
        ["QB::Field::Numeric::Formula"] = "Number",
        ["QB::Field::Currency::Formula"] = "Number",
        ["QB::Field::Percent::Formula"] = "Number",
        ["QB::Field::Date::Formula"] = "Date",
        ["QB::Field::DateTime::Formula"] = "DateTime",
        ["QB::Field::Duration::Formula"] = "Duration",
        ["QB::Field::User::Formula"] = "User",
    };

    /// <summary>Formula field types with no PowerBase <c>FormulaResultTypes</c> equivalent —
    /// e.g. a multi-select formula has no true list/multi-value result type in PowerBase's
    /// formula model ({Text, Number, Date, DateTime, Duration, Bool, User}). Coercing it to Text
    /// would flatten a multi-value result into a single string, a real semantic change — flagged
    /// unsupported rather than silently approximated.</summary>
    public static readonly IReadOnlyCollection<string> UnsupportedFormulaTypes = new[]
    {
        "QB::Field::MultiselectText::Formula",
        "QB::Field::TextMultipleChoice::Formula",
        "QB::Field::TimeOfDay::Formula",
    };

    /// <summary>Relationship-family field types — never created as a standalone <c>PblField</c>;
    /// the QBL→PBL converter folds these into the owning <c>PblRelationship</c> instead (Slice 2).</summary>
    public static readonly IReadOnlyCollection<string> RelationshipFieldTypes = new[]
    {
        "QB::Field::Reference",
        "QB::Field::ReferenceProxy",
        "QB::Field::Lookup",
        "QB::Field::Summary",
        "QB::Field::ReportLink",
    };

    /// <summary>System fields Quickbase always includes per table. PowerBase's own
    /// <c>IAppSeeder</c> already auto-seeds equivalents — these are skipped from the imported
    /// field list with an informational note, not a warning (expected, not a gap).</summary>
    public static readonly IReadOnlyCollection<string> SystemFieldTypes = new[]
    {
        "QB::Field::RecordID",
        "QB::Field::RecordOwner",
        "QB::Field::DateCreated",
        "QB::Field::DateModified",
        "QB::Field::LastModifiedBy",
    };

    /// <summary>QBL system field type → the Name PowerBase's own <c>IAppSeeder</c> gives the
    /// equivalent auto-seeded field (see <c>AppSeeder.cs</c>'s <c>systemFieldDefs</c>). System
    /// fields never become a standalone <c>PblField</c> (PowerBase already seeds one per table),
    /// but a real, common Quickbase pattern is a relationship Summary/Lookup that targets the
    /// built-in Record ID# field directly (e.g. <c>Function: Maximum</c> over
    /// <c>QB::Field::RecordID</c>, used to compute "latest related record"). Without this name
    /// registered, every such Summary/Lookup — and everything chained off it (reports, other
    /// lookups, form elements) — would stay permanently unresolved.</summary>
    public static readonly IReadOnlyDictionary<string, string> SystemFieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["QB::Field::RecordID"] = "Record ID#",
        ["QB::Field::DateCreated"] = "Date Created",
        ["QB::Field::DateModified"] = "Date Modified",
        ["QB::Field::RecordOwner"] = "Record Owner",
        ["QB::Field::LastModifiedBy"] = "Last Modified By",
    };

    /// <summary>Address sub-component shadow fields. Quickbase exports each address
    /// sub-component (street/city/state/zip/country) as its own field alongside the composite
    /// <c>QB::Field::Address</c> field — importing them as separate scalar fields would create
    /// duplicates of data the composite Address field already carries. Skipped informationally;
    /// only the composite field is imported.</summary>
    public static readonly IReadOnlyCollection<string> AddressSubComponentTypes = new[]
    {
        "QB::Field::AddressStreet1",
        "QB::Field::AddressStreet2",
        "QB::Field::AddressCity",
        "QB::Field::AddressState",
        "QB::Field::AddressPostalCode",
        "QB::Field::AddressCountry",
    };

    /// <summary>QBL's composite Address field explicitly links to each of its own shadow
    /// sub-component fields via a <c>!Ref</c> property (confirmed real shape: <c>Street1</c>,
    /// <c>Street2</c>, <c>City</c>, <c>State</c>, <c>PostalCode</c>, <c>Country</c>) — this maps
    /// that QBL property name to the canonical PowerBase
    /// <see cref="PowerBase.Domain.FieldSettings.AddressSubFields"/> key.</summary>
    public static readonly IReadOnlyDictionary<string, string> AddressSubComponentPropertyToKey = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Street1"] = "street1",
        ["Street2"] = "street2",
        ["City"] = "city",
        ["State"] = "state",
        ["PostalCode"] = "zip",
        ["Country"] = "country",
    };

    /// <summary>QBL types with no PowerBase equivalent. Always flagged, never silently dropped.
    /// <c>QB::Field::Unsupported</c> is Quickbase's own escape hatch (carries an
    /// <c>Explanation</c> property) for a construct even Quickbase's export couldn't map.</summary>
    public static readonly IReadOnlyCollection<string> Unsupported = new[]
    {
        "QB::Field::Predecessor",
        "QB::Field::WorkDate",
        "QB::Field::Unsupported",
        "QB::Field::VCard",
        "QB::Field::ICalendar",
    };

    public static bool TryMapScalar(string qblType, out string powerBaseTypeCode) =>
        ScalarTypes.TryGetValue(qblType, out powerBaseTypeCode!);

    public static bool TryMapFormulaResultType(string qblType, out string resultType) =>
        FormulaResultTypes.TryGetValue(qblType, out resultType!);
}
