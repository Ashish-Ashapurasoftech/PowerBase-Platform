namespace PowerBase.Application.Fields.Commands.SetKey;

/// <summary>Sets (or resets) a table's key field. <paramref name="FieldFid"/> null (or 3, the system
/// Record ID# field) resets to the default key. <paramref name="Force"/> confirms the cascade rewire
/// when the table already has parent-side relationships.</summary>
public record SetKeyCommand(Guid TablePublicId, int? FieldFid, bool Force = false);
