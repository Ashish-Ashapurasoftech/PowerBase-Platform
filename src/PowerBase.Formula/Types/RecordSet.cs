namespace PowerBase.Formula.Types;

/// <summary>
/// A set of records produced by the cross-table functions (<c>GetRecords</c>,
/// <c>GetRecordByUniqueField</c>, <c>GetRecord</c>): the target table's id and the
/// matching record ids. Consumed by <c>GetFieldValues</c>/<c>SumValues</c>/<c>Count</c>.
/// <see cref="TableId"/> is empty when the set refers to the current (self) table.
/// </summary>
public sealed record RecordSet(string TableId, IReadOnlyList<long> RecordIds)
{
    public static readonly RecordSet Empty = new(string.Empty, System.Array.Empty<long>());
}
