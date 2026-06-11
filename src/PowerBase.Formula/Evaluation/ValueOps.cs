using PowerBase.Formula.Types;

namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Equality and ordering over <see cref="FormulaValue"/>, shared by the evaluator's
/// <c>=</c>/<c>&lt;</c> operators and by <c>Case</c>. Text equality is
/// case-sensitive; user equality compares ids case-insensitively.
/// </summary>
internal static class ValueOps
{
    public static bool AreEqual(FormulaValue l, FormulaValue r)
    {
        if (l.IsNull && r.IsNull) return true;
        if (l.IsNull || r.IsNull) return false;
        if (l.Type != r.Type) return false;

        return l.Type switch
        {
            FormulaType.Number => l.AsNumber() == r.AsNumber(),
            FormulaType.Text => string.Equals(l.AsText(), r.AsText(), StringComparison.Ordinal),
            FormulaType.Bool => l.AsBool() == r.AsBool(),
            FormulaType.Date => l.AsDate() == r.AsDate(),
            FormulaType.DateTime => l.AsDateTime() == r.AsDateTime(),
            FormulaType.Duration => l.AsDuration() == r.AsDuration(),
            FormulaType.User => string.Equals(l.AsUser().UserId, r.AsUser().UserId, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Compares two non-null values of the same orderable type.</summary>
    public static int Compare(FormulaValue l, FormulaValue r) => l.Type switch
    {
        FormulaType.Number => l.AsNumber().CompareTo(r.AsNumber()),
        FormulaType.Date => l.AsDate().CompareTo(r.AsDate()),
        FormulaType.DateTime => l.AsDateTime().CompareTo(r.AsDateTime()),
        FormulaType.Duration => l.AsDuration().CompareTo(r.AsDuration()),
        FormulaType.Text => string.CompareOrdinal(l.AsText(), r.AsText()),
        _ => 0,
    };
}
