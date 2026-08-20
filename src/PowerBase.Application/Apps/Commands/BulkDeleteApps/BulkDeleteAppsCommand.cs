namespace PowerBase.Application.Apps.Commands.BulkDeleteApps;

public record BulkDeleteAppsCommand(IReadOnlyList<Guid> PublicIds);
