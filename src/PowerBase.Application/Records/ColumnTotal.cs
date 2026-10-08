namespace PowerBase.Application.Records;

/// <summary>
/// Raw aggregates of one report column across EVERY record that matches the report's filters —
/// not just the page being returned. Formatting, and the Sum/Average opt-ins, stay with the
/// caller: Sum is the plain total, an average is Sum / RowCount when the field counts blanks as 0
/// (its default) or Sum / NonBlankCount when it doesn't. For a checkbox column Sum is the number
/// of checked records.
/// </summary>
public sealed class ColumnTotal
{
    public decimal Sum { get; init; }
    /// <summary>Records whose value in this column is not blank.</summary>
    public long NonBlankCount { get; init; }
    /// <summary>All records matching the report's filters, blank or not.</summary>
    public long RowCount { get; init; }
}
