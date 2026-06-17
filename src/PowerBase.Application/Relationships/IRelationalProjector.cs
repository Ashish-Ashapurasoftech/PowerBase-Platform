using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Computes Lookup and Summary field values at read time by resolving them across the
/// relationship (cross-table). Returns one map per input row, keyed by the lookup/summary
/// field's Fid. Runs before the formula projector so formulas can reference lookup values.
/// </summary>
public interface IRelationalProjector
{
    Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default);
}
