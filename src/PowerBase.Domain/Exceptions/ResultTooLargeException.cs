namespace PowerBase.Domain.Exceptions;

/// <summary>
/// The request can only be answered by working out calculated (or encrypted) values in memory over more records than one
/// request may read. Thrown instead of answering from the first records only: a partial list, count or file passed off as
/// complete would be wrong. The caller narrows the question (a filter on a stored field) or uses a path that reads in pages.
/// </summary>
public class ResultTooLargeException : DomainException
{
    public const string Code = "RESULT_TOO_LARGE";

    /// <summary>The most records the operation may evaluate in memory.</summary>
    public int Limit { get; }

    public ResultTooLargeException(string operation, int limit)
        : base(Code, $"{operation} needs to evaluate calculated values over more than {limit:N0} records, which cannot be done in one request. " +
                     "Add a filter on a stored field to narrow the records, or wait until the calculated field's stored values are ready.")
    {
        Limit = limit;
    }
}
