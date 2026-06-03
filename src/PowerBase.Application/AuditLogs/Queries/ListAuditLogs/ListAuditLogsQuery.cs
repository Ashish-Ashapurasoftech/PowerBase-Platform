namespace PowerBase.Application.AuditLogs.Queries.ListAuditLogs;

public record ListAuditLogsQuery(
    Guid? AppPublicId,
    DateTime? From,
    DateTime? To,
    string? Email,
    string? EntityType,
    string? EntityId,
    string? Action,
    int Page = 1,
    int PageSize = 50);
