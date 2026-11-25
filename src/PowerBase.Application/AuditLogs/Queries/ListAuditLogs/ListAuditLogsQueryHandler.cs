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
    private readonly IAppTableRepository _tableRepo;

    public ListAuditLogsQueryHandler(IAuditRepository auditRepo, IAppRepository appRepo, IAppTableRepository tableRepo)
    {
        _auditRepo = auditRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<AuditLogListResult> HandleAsync(ListAuditLogsQuery query, CancellationToken ct = default)
    {
        long? appId = null;
        if (query.AppPublicId.HasValue)
        {
            var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId.Value, ct);
            appId = app.Id;
        }

        long? tableId = null;
        if (query.TablePublicId.HasValue)
        {
            var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId.Value, ct);
            tableId = table.Id;
        }

        var filter = new ActivityLogFilter(
            From: query.From,
            To: query.To,
            AppId: appId,
            Email: query.Email,
            EntityType: query.EntityType,
            EntityId: query.EntityId,
            Action: query.Action,
            Page: query.Page,
            PageSize: Math.Clamp(query.PageSize, 1, 200),
            TableId: tableId,
            TablePublicId: query.TablePublicId,
            RecordNumber: query.RecordNumber);

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
