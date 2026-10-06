namespace PowerBase.Application.FieldReferences;

/// <summary>Gathers the rows of ONE source while an extractor walks it. <see cref="FieldReferenceRow"/>
/// has value equality, so the same field used the same way twice (a column listed twice, a field in
/// two conditions) collapses into a single row — which is also what meta.FieldReference's unique
/// key requires.</summary>
public sealed class FieldReferenceCollector
{
    private readonly HashSet<FieldReferenceRow> _rows = [];
    private readonly string _sourceType;
    private readonly long _sourceId;
    private readonly long _sourceTableId;

    public FieldReferenceCollector(string sourceType, long sourceId, long sourceTableId)
    {
        _sourceType = sourceType;
        _sourceId = sourceId;
        _sourceTableId = sourceTableId;
    }

    public IReadOnlyCollection<FieldReferenceRow> Rows => _rows;

    /// <summary>Records a use of the field with this meta.AppField.Id. Null (an id that didn't
    /// resolve — a deleted field, an unset slot) is ignored.</summary>
    public void Add(long? targetFieldId, string usage)
    {
        if (targetFieldId is { } id and > 0)
            _rows.Add(new FieldReferenceRow(_sourceType, _sourceId, _sourceTableId, id, usage));
    }

    public void AddAll(IEnumerable<long> targetFieldIds, string usage)
    {
        foreach (var id in targetFieldIds) Add(id, usage);
    }
}
