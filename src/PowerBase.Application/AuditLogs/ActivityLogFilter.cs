namespace PowerBase.Application.AuditLogs;

public record ActivityLogFilter(
    DateTime? From,
    DateTime? To,
    long? AppId,
    string? Email,
    string? EntityType,
    string? EntityId,
    string? Action,
    int Page = 1,
    int PageSize = 50);
