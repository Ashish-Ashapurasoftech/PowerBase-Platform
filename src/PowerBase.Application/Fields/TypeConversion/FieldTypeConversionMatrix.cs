namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// Single source of truth for which field type may change into which, what kind of data move that is,
/// and how much data it can lose. Pure — no repositories; the Change Type preview/execute handlers and the
/// frontend's target dropdown (via the API) both read from here. Rules: docs/field-type-conversion.md.
/// </summary>
public static class FieldTypeConversionMatrix
{
    public static readonly IReadOnlyList<string> SupportedScalarTypes =
    [
        "Text", "TextMultiLine", "RichText", "SingleSelect",
        "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration",
        "Boolean", "Email", "Phone", "Url", "File", "User",
    ];

    /// <summary>Each supported formula type and the scalar type its evaluated value behaves like.</summary>
    private static readonly IReadOnlyDictionary<string, string> FormulaScalarTwins = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Formula_Text"] = "Text",
        ["Formula_Number"] = "Number",
        ["Formula_Date"] = "Date",
        ["Formula_DateTime"] = "DateTime",
        ["Formula_Duration"] = "Duration",
        ["Formula_Bool"] = "Boolean",
        ["Formula_User"] = "User",
        ["Formula_Url"] = "Url",
        ["Formula_Time"] = "Time",
        ["Formula_Phone"] = "Phone",
        ["Formula_Email"] = "Email",
        ["Formula_RichText"] = "RichText",
    };

    public static IReadOnlyCollection<string> SupportedFormulaTypes => (IReadOnlyCollection<string>)FormulaScalarTwins.Keys;

    /// <summary>Types a relationship key field may hold — mirrors SetKeyCommandHandler's eligible list.</summary>
    public static readonly IReadOnlySet<string> KeyEligibleTypes =
        new HashSet<string>(StringComparer.Ordinal) { "Text", "Number", "Currency", "Percent", "Rating", "Date" };

    private static readonly IReadOnlySet<string> NumericFamily =
        new HashSet<string>(StringComparer.Ordinal) { "Number", "Currency", "Percent", "Rating" };

    /// <summary>Scalar pairs (besides → Text) where every value survives.</summary>
    private static readonly IReadOnlySet<(string, string)> LosslessPairs = new HashSet<(string, string)>
    {
        ("Date", "DateTime"),
        ("Email", "Url"), ("Email", "SingleSelect"), ("Url", "SingleSelect"), ("Text", "SingleSelect"),
        ("Boolean", "Number"), ("Boolean", "Currency"), ("Boolean", "Rating"),
        ("Number", "Currency"), ("Currency", "Number"), ("Number", "Rating"), ("Currency", "Rating"),
        ("Number", "Percent"), ("Currency", "Percent"), ("Percent", "Number"), ("Percent", "Currency"),
    };

    public static bool IsFormula(string typeCode) => FormulaScalarTwins.ContainsKey(typeCode);

    public static bool IsSupported(string typeCode) =>
        IsFormula(typeCode) || SupportedScalarTypes.Contains(typeCode, StringComparer.Ordinal);

    /// <summary>The scalar type whose value rules apply to <paramref name="typeCode"/>'s values
    /// (itself for scalars, the twin for formulas).</summary>
    public static string ValueTypeOf(string typeCode) =>
        FormulaScalarTwins.TryGetValue(typeCode, out var twin) ? twin : typeCode;

    public static IReadOnlyList<string> AllSupportedTypes { get; } =
        [.. SupportedScalarTypes, .. FormulaScalarTwins.Keys];

    /// <summary>Every target <paramref name="sourceTypeCode"/> may change into, in dropdown order, with its check.</summary>
    public static IReadOnlyList<ConversionCheck> GetAllowedTargets(string sourceTypeCode, FieldConversionConstraints? constraints = null) =>
        AllSupportedTypes
            .Select(target => Check(sourceTypeCode, target, constraints))
            .Where(c => c.IsAllowed)
            .ToList();

    public static ConversionCheck Check(string sourceTypeCode, string targetTypeCode, FieldConversionConstraints? constraints = null)
    {
        constraints ??= FieldConversionConstraints.None;

        if (constraints.IsSystemField)
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, "System fields can't change type.");
        if (!IsSupported(sourceTypeCode))
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, $"Changing the type of a {sourceTypeCode} field isn't supported.");
        if (!IsSupported(targetTypeCode))
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, $"Changing a field to {targetTypeCode} isn't supported.");
        if (sourceTypeCode == targetTypeCode)
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, "The field already has this type.");
        if (constraints.IsRelationshipKey && !KeyEligibleTypes.Contains(targetTypeCode))
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode,
                "This field is a relationship key; it can only become Text, Numeric, Currency, Percent, Rating or Date.");

        var sourceIsFormula = IsFormula(sourceTypeCode);
        var targetIsFormula = IsFormula(targetTypeCode);

        if (sourceIsFormula && targetIsFormula)
            return ConversionCheck.Allowed(sourceTypeCode, targetTypeCode, ConversionKind.FormulaToFormula, DataLossLevel.None);
        if (targetIsFormula)
            return ConversionCheck.Allowed(sourceTypeCode, targetTypeCode, ConversionKind.ScalarToFormula, DataLossLevel.Destructive);

        // File holds blobs: as a source it only turns into its file names; nothing evaluates into a File.
        if (sourceTypeCode == "File" && targetTypeCode is not ("Text" or "TextMultiLine"))
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, "A File Attachment field can only become a text field.");
        if (sourceIsFormula && targetTypeCode == "File")
            return ConversionCheck.Blocked(sourceTypeCode, targetTypeCode, "A formula can't become a File Attachment field.");

        if (sourceIsFormula)
            return ConversionCheck.Allowed(sourceTypeCode, targetTypeCode, ConversionKind.FormulaToScalar,
                LossFor(ValueTypeOf(sourceTypeCode), targetTypeCode));

        var kind = NumericFamily.Contains(sourceTypeCode) && NumericFamily.Contains(targetTypeCode)
            ? ConversionKind.NumericFamily
            : ConversionKind.ScalarToScalar;
        return ConversionCheck.Allowed(sourceTypeCode, targetTypeCode, kind, LossFor(sourceTypeCode, targetTypeCode));
    }

    private static DataLossLevel LossFor(string valueType, string target)
    {
        if (target == "File") return DataLossLevel.Destructive;
        if (valueType == target) return DataLossLevel.None;           // formula → its own scalar twin
        if (target is "Text" or "TextMultiLine" or "RichText") return DataLossLevel.None;
        return LosslessPairs.Contains((valueType, target)) ? DataLossLevel.None : DataLossLevel.Possible;
    }
}
