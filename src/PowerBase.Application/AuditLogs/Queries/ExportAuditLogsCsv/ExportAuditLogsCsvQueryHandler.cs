using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.AuditLogs.Queries.ExportAuditLogsCsv;

public class ExportAuditLogsCsvQueryHandler
{
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;

    public ExportAuditLogsCsvQueryHandler(IAuditRepository auditRepo, IAppRepository appRepo)
    {
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<byte[]> HandleAsync(ExportAuditLogsCsvQuery query, CancellationToken ct = default)
    {
        long? appId = null;
        if (query.AppPublicId.HasValue)
        {
            var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId.Value, ct);
            appId = app.Id;
        }

        var filter = new ActivityLogFilter(
            From: query.From,
            To: query.To,
            AppId: appId,
            Email: query.Email,
            EntityType: query.EntityType,
            EntityId: query.EntityId,
            Action: query.Action);

        var rows = await _auditRepo.ExportActivityLogsAsync(filter, ct);
        return BuildCsv(rows);
    }

    private static byte[] BuildCsv(IReadOnlyList<ActivityLog> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OccurredOn,UserEmail,UserName,Action,EntityType,EntityId,IpAddress");

        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',',
                CsvField(r.OccurredOn.ToString("O")),
                CsvField(r.UserEmail),
                CsvField(r.UserName),
                CsvField(r.Action),
                CsvField(r.EntityType),
                CsvField(r.EntityId),
                CsvField(r.IpAddress)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string? value)
    {
        if (value is null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
