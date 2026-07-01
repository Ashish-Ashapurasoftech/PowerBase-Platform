using PowerBase.Formula.Querying;

namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Supplies a single record's stored field values to the evaluator, keyed by Fid.
/// Returns the raw stored value (string/decimal/DateTime/bool/…) or null when the
/// field is blank/absent; the evaluator coerces it to the field's declared
/// <see cref="Types.FormulaType"/>.
///
/// The cross-table members (default no-ops) back the Tier-2 record functions
/// (<c>GetRecords</c>, <c>GetFieldValues</c>, …); single-record contexts inherit the
/// defaults and those functions resolve to empty results.
/// </summary>
public interface IRecordContext
{
    object? GetValue(long fid);

    /// <summary>Record ids in <paramref name="tableId"/> (empty ⇒ current table) matching <paramref name="query"/>.</summary>
    IReadOnlyList<long> QueryRecords(string tableId, RecordQuery query) => System.Array.Empty<long>();

    /// <summary>The record id in <paramref name="tableId"/> whose field <paramref name="fid"/> equals <paramref name="value"/>, or null.</summary>
    long? FindRecordByField(string tableId, long fid, string value) => null;

    /// <summary>Whether record <paramref name="recordId"/> exists in <paramref name="tableId"/> (empty ⇒ current table).</summary>
    bool RecordExists(string tableId, long recordId) => false;

    /// <summary>The raw values of field <paramref name="fid"/> across <paramref name="recordIds"/> in <paramref name="tableId"/>.</summary>
    IReadOnlyList<object?> GetFieldValues(string tableId, IReadOnlyList<long> recordIds, long fid) => System.Array.Empty<object?>();
}
