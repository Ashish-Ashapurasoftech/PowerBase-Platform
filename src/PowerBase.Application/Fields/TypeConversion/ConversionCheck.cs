namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>How a field's data moves when its type changes. See docs/field-type-conversion.md.</summary>
public enum ConversionKind
{
    /// <summary>Every stored value is re-typed by <see cref="FieldValueConverter"/>.</summary>
    ScalarToScalar,
    /// <summary>Number ↔ Currency ↔ Percent ↔ Rating — same column, Percent rescaled ×/÷100 (the existing Display-As switch).</summary>
    NumericFamily,
    /// <summary>Evaluated formula values are converted and stored; the expression is discarded.</summary>
    FormulaToScalar,
    /// <summary>Stored data is discarded; settings reset to defaults (no old expression restored).</summary>
    ScalarToFormula,
    /// <summary>No stored data; the expression is kept and recompiled against the new result type.</summary>
    FormulaToFormula,
}

/// <summary>How much existing data the conversion can lose — drives the warning shown before Change Type.</summary>
public enum DataLossLevel
{
    /// <summary>Every value survives (e.g. anything → Text, Date → DateTime).</summary>
    None,
    /// <summary>Values that don't fit the new type become blank.</summary>
    Possible,
    /// <summary>All existing values are discarded (→ File, Scalar → Formula).</summary>
    Destructive,
}

/// <summary>Result of asking the matrix whether <see cref="SourceTypeCode"/> may become <see cref="TargetTypeCode"/>.</summary>
public sealed record ConversionCheck(
    string SourceTypeCode,
    string TargetTypeCode,
    bool IsAllowed,
    ConversionKind? Kind,
    DataLossLevel? Loss,
    string? BlockedReason)
{
    public static ConversionCheck Blocked(string source, string target, string reason) =>
        new(source, target, false, null, null, reason);

    public static ConversionCheck Allowed(string source, string target, ConversionKind kind, DataLossLevel loss) =>
        new(source, target, true, kind, loss, null);
}

/// <summary>Facts about the field (not its type) that restrict which targets are allowed.</summary>
/// <param name="IsSystemField">Record ID#, Date Created, … — never convertible.</param>
/// <param name="IsRelationshipKey">The field is a parent table's key / display key — only key-eligible targets.</param>
public sealed record FieldConversionConstraints(bool IsSystemField = false, bool IsRelationshipKey = false)
{
    public static readonly FieldConversionConstraints None = new();
}
