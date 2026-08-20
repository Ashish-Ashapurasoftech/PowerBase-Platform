namespace PowerBase.Domain.Exceptions;

/// <summary>A single field-constraint failure for a specific record, produced by mass-update
/// (and available for any other multi-record validation pass). <paramref name="ConstraintType"/>
/// is one of "Required", "Unique", "NotFound".</summary>
public record RecordConstraintViolation(Guid RecordId, long FieldId, string ConstraintType, string Message);

/// <summary>
/// Thrown when one or more records fail field-level constraint validation (Required/Unique) during
/// a multi-record operation. Unlike <see cref="ValidationException"/> (a flat field→messages map,
/// for single-resource request-shape errors), this carries a structured, per-record violation list —
/// callers need to know exactly which record and which field failed, and why.
/// </summary>
public class RecordConstraintViolationException : DomainException
{
    public IReadOnlyList<RecordConstraintViolation> Violations { get; }

    public RecordConstraintViolationException(IReadOnlyList<RecordConstraintViolation> violations)
        : base("RECORD_CONSTRAINT_VIOLATION", "One or more records failed field constraint validation.")
    {
        Violations = violations;
    }
}
