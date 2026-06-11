namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Supplies a single record's stored field values to the evaluator, keyed by Fid.
/// Returns the raw stored value (string/decimal/DateTime/bool/…) or null when the
/// field is blank/absent; the evaluator coerces it to the field's declared
/// <see cref="Types.FormulaType"/>.
///
/// This is an interface so a future cross-table context (related/aggregated
/// values) can satisfy Tier-2 formulas without changing the evaluator.
/// </summary>
public interface IRecordContext
{
    object? GetValue(long fid);
}
