using PowerBase.Domain.Entities;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Computes Formula-field values for already-fetched record rows (compute-on-read).
/// Formula fields have no physical column, so their values are projected in after
/// the repository returns base rows.
/// </summary>
public interface IFormulaProjector
{
    /// <summary>
    /// Returns a list parallel to <paramref name="rows"/>; each entry maps a Formula
    /// field's Fid to its computed value (or null on blank/error). Returns empty maps
    /// when the table has no Formula fields.
    /// </summary>
    IReadOnlyList<IReadOnlyDictionary<long, object?>> Project(
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows);
}
