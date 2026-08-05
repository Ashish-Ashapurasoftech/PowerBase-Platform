namespace PowerBase.API.Models.Apps;

public record BulkDeleteAppsRequest(List<Guid> PublicIds);
