namespace PowerBase.Application.Records.Commands.MassUpdateRecords;

/// <summary>Applies the same set of field/value pairs to every listed record in one operation
/// (Quickbase-style Mass Update). All-or-nothing: every record and field is validated against
/// meta.AppField's Required/Unique constraints before anything is written.</summary>
public record MassUpdateRecordsCommand(
    Guid TablePublicId,
    IReadOnlyList<Guid> RecordPublicIds,
    IReadOnlyDictionary<long, object?> FieldValues);
