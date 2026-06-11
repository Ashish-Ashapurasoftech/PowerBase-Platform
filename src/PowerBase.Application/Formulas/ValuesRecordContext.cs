using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="IRecordContext"/> over a field-id → value map. Used by the evaluate
/// API, where the caller supplies in-flight form values keyed by field id (rather
/// than a stored row keyed by physical column).
/// </summary>
public sealed class ValuesRecordContext : IRecordContext
{
    private readonly IReadOnlyDictionary<long, object?> _values;

    public ValuesRecordContext(IReadOnlyDictionary<long, object?> values) => _values = values;

    public object? GetValue(long fid) => _values.TryGetValue(fid, out var v) ? v : null;
}
