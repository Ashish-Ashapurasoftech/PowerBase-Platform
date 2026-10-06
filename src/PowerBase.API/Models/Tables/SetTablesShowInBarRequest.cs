namespace PowerBase.API.Models.Tables;

public record SetTablesShowInBarRequest(List<Guid> PublicIds, bool IsShowInBar);
