namespace PowerBase.Domain.Exceptions;

/// <summary>One failed check on one row of a bulk create. <paramref name="RowIndex"/> is the row's
/// 0-based position in the request (new rows have no record id yet); <paramref name="FieldId"/> is the
/// field's Fid, or 0 when the failure belongs to the whole row (Custom Data Rule, Prevent Save rule).</summary>
public record BulkCreateRowViolation(int RowIndex, long FieldId, string ConstraintType, string Message);

/// <summary>
/// Thrown when one or more rows of a bulk create fail validation. Nothing is written: the caller gets
/// every failing row and field at once, so the user can fix all of them before saving again.
/// </summary>
public class BulkCreateValidationException : DomainException
{
    private const int MaxMessagesInSummary = 3;

    public IReadOnlyList<BulkCreateRowViolation> Violations { get; }

    public BulkCreateValidationException(IReadOnlyList<BulkCreateRowViolation> violations)
        : base("BULK_CREATE_VALIDATION_FAILED", BuildMessage(violations))
    {
        Violations = violations;
    }

    /// <summary>A readable summary for a toast, e.g. "Row 2: 'Qty' is required. Row 5: … (and 4 more)".
    /// Rows are numbered from 1 here because this text is shown to people.</summary>
    private static string BuildMessage(IReadOnlyList<BulkCreateRowViolation> violations)
    {
        var messages = violations.Select(v => $"Row {v.RowIndex + 1}: {v.Message}").Distinct().ToList();
        if (messages.Count == 0) return "One or more rows failed validation.";
        var summary = string.Join(" ", messages.Take(MaxMessagesInSummary));
        return messages.Count > MaxMessagesInSummary ? $"{summary} (and {messages.Count - MaxMessagesInSummary} more)" : summary;
    }
}
