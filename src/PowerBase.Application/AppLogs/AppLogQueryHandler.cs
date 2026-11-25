using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.AppLogs;

public sealed record ListAppLogsQuery(
    Guid AppPublicId,
    DateTime? From,
    DateTime? To,
    string? Email,
    string? EntityType,
    string? Action,
    Guid? TablePublicId,
    long? RecordNumber,
    int Page = 1,
    int PageSize = 50);

/// <summary>Reads the application log (logs database) for one app of the current tenant: the list, and one row's details.</summary>
public sealed class AppLogQueryHandler
{
    private readonly IAppLogRepository _logs;
    private readonly IAppRepository _apps;
    private readonly IAppTableRepository _tables;
    private readonly IQueryContext _queryContext;

    public AppLogQueryHandler(IAppLogRepository logs, IAppRepository apps, IAppTableRepository tables, IQueryContext queryContext)
    {
        _logs = logs;
        _apps = apps;
        _tables = tables;
        _queryContext = queryContext;
    }

    public async Task<(IReadOnlyList<AppLogEntry> Items, int Total, int Page, int PageSize)> ListAsync(ListAppLogsQuery query, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var appId = await _apps.GetIdByPublicIdAsync(query.AppPublicId, ct);
        if (appId <= 0) throw new NotFoundException("App", query.AppPublicId);
        long? tableId = query.TablePublicId is { } t ? (await _tables.GetByPublicIdAsync(t, ct)).Id : null;
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(query.Page, 1);

        var (items, total) = await _logs.QueryAsync(new AppLogFilter(
            tenantId, appId, query.From, query.To, query.Email, query.EntityType, query.Action, tableId, query.RecordNumber, page, pageSize), ct);
        return (items, total, page, pageSize);
    }

    public async Task<AppLogEntry> GetAsync(long id, CancellationToken ct = default) =>
        await _logs.GetAsync(RequireTenant(), id, ct) ?? throw new NotFoundException("AppLog", id);

    private long RequireTenant() =>
        _queryContext.TenantId > 0 ? _queryContext.TenantId : throw new UnauthorizedActionException("read the application log without a tenant");
}
