using PowerBase.Domain.Constants;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="IRecordContext"/> over a repository row dictionary, which is keyed
/// by physical column name (<c>f_{fid}</c>). Resolves a field's Fid to its stored
/// raw value.
/// </summary>
public sealed class RowRecordContext : IRecordContext
{
    private readonly IReadOnlyDictionary<string, object?> _row;

    public RowRecordContext(IReadOnlyDictionary<string, object?> row) => _row = row;

    public object? GetValue(long fid)
        => _row.TryGetValue(PhysicalNaming.ColumnName((int)fid), out var v) ? v : null;
}
