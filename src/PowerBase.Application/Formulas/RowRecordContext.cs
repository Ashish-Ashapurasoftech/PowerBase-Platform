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
    private readonly IReadOnlyDictionary<long, string> _fidToColMap;

    public RowRecordContext(IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<long, string> fidToColMap)
    {
        _row = row;
        _fidToColMap = fidToColMap;
    }

    public object? GetValue(long fid)
    {
        if (_fidToColMap.TryGetValue(fid, out var colName) && !string.IsNullOrEmpty(colName))
            return _row.TryGetValue(colName, out var v) ? v : null;
            
        return _row.TryGetValue(PhysicalNaming.ColumnName((int)fid), out var fbv) ? fbv : null;
    }
}
