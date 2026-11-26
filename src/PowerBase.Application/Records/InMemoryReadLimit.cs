using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records;

/// <summary>
/// The most records a request evaluates in memory. Sorting or filtering by a calculated (Formula / Lookup / Summary) field that
/// has no stored column, or by an encrypted field, happens in memory over one read of the matches. That read is bounded so a
/// request can never pull millions of rows into the API. It is a budget, not a cut: <see cref="IRecordRepository.ListAllMatchingAsync"/>
/// reads <see cref="ReadRows"/> (one more than <see cref="Rows"/>), so a caller can tell "every match was read" from "there are
/// more", and either answers completely or says it could not (a report flags it, every other path throws
/// <see cref="ResultTooLargeException"/>). Nothing returns the first <see cref="Rows"/> records as if they were all of them.
/// </summary>
public static class InMemoryReadLimit
{
    public const int Rows = 50_000;

    /// <summary>What one in-memory read asks SQL for: one row more than <see cref="Rows"/>, to detect that there are more.</summary>
    public const int ReadRows = Rows + 1;

    /// <summary>True when a read of <see cref="ReadRows"/> returned every match.</summary>
    public static bool IsComplete(int rowsRead) => rowsRead <= Rows;

    /// <summary>Throws <see cref="ResultTooLargeException"/> unless the read returned every match.</summary>
    public static void EnsureComplete(int rowsRead, string operation)
    {
        if (!IsComplete(rowsRead)) throw new ResultTooLargeException(operation, Rows);
    }
}
