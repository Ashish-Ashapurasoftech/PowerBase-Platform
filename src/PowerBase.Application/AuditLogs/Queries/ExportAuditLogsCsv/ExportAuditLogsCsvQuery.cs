namespace PowerBase.Application.AuditLogs.Queries.ExportAuditLogsCsv;

public record ExportAuditLogsCsvQuery(
    Guid? AppPublicId,
    DateTime? From,
    DateTime? To,
    string? Email,
    string? EntityType,
    string? Action);
