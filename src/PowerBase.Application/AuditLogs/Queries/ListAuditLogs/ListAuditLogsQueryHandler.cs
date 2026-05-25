using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.AuditLogs.Queries.ListAuditLogs;

public class AuditLogListResult
{
    public IReadOnlyList<ActivityLog> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListAuditLogsQueryHandler
{
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;

    public ListAuditLogsQueryHandler(IAuditRepository auditRepo, IAppRepository appRepo)
    {
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<AuditLogListResult> HandleAsync(ListAuditLogsQuery query, CancellationToken ct = default)
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
            Action: query.Action,
            Page: query.Page,
            PageSize: Math.Clamp(query.PageSize, 1, 200));

        var (items, total) = await _auditRepo.QueryActivityLogsAsync(filter, ct);

        return new AuditLogListResult
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = filter.PageSize,
        };
    }
}
