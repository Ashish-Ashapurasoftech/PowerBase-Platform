namespace PowerBase.Application.Fields.Commands.SetKey;

/// <summary>Sets (or resets) a table's key field. <paramref name="FieldFid"/> null (or 3, the system
/// Record ID# field) resets to the default key. The key field is a display/identity choice only and
/// never rewrites existing relationship data, so no confirmation flag is needed.</summary>
public record SetKeyCommand(Guid TablePublicId, int? FieldFid);
