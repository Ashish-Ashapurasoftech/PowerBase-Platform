namespace PowerBase.Application.Tables.Commands.SetTablesShowInBar;

public record SetTablesShowInBarCommand(Guid AppPublicId, IReadOnlyList<Guid> PublicIds, bool IsShowInBar);
