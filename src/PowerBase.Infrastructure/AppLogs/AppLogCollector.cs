using PowerBase.Application.AppLogs;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.AppLogs;

/// <summary>Scoped <see cref="IAppLogCollector"/>; see its summary. The AppLogMiddleware calls <see cref="BeginRequest"/> /
/// <see cref="TakeEvents"/>; without them (pipelines, workers) every event is queued straight away.</summary>
public sealed class AppLogCollector : IAppLogCollector
{
    private readonly IQueryContext _queryContext;
    private readonly IAppLogQueue _queue;
    private readonly object _gate = new();
    private readonly List<AppLogEvent> _events = [];
    private readonly Dictionary<string, (string? Old, string? New)> _pendingValues = new(StringComparer.OrdinalIgnoreCase);
    private bool _inRequest;

    public AppLogCollector(IQueryContext queryContext, IAppLogQueue queue)
    {
        _queryContext = queryContext;
        _queue = queue;
    }

    public void BeginRequest()
    {
        lock (_gate) _inRequest = true;
    }

    public IReadOnlyList<AppLogEvent> TakeEvents()
    {
        lock (_gate)
        {
            var taken = _events.ToList();
            _events.Clear();
            _pendingValues.Clear();
            return taken;
        }
    }

    public void AttachValues(string entityId, string? oldValues, string? newValues)
    {
        if (string.IsNullOrEmpty(entityId) || (oldValues is null && newValues is null)) return;
        lock (_gate) _pendingValues[entityId] = (oldValues, newValues);
    }

    public void AddEvent(AppLogEvent appLogEvent)
    {
        AppLogEvent withValues;
        bool inRequest;
        lock (_gate)
        {
            withValues = appLogEvent;
            if (appLogEvent.EntityId is { } id && _pendingValues.Remove(id, out var values))
                withValues = appLogEvent with { OldValues = values.Old ?? appLogEvent.OldValues, NewValues = values.New ?? appLogEvent.NewValues };
            inRequest = _inRequest;
            if (inRequest) _events.Add(withValues);
        }
        if (!inRequest) _queue.Enqueue(ToEntry(withValues, _queryContext));
    }

    /// <summary>The log row for an event, with who did it; the middleware adds the request's own details.</summary>
    public static AppLogEntry ToEntry(AppLogEvent e, IQueryContext who) => new()
    {
        TenantId = who.TenantId > 0 ? who.TenantId : null,
        AppId = e.AppId,
        TableId = e.TableId,
        RecordNumber = e.RecordNumber,
        UserId = who.UserId > 0 ? who.UserId : null,
        UserName = string.IsNullOrEmpty(who.UserName) ? null : who.UserName,
        UserEmail = string.IsNullOrEmpty(who.UserEmail) ? null : who.UserEmail,
        Action = e.Action,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Description = e.Description,
        OldValues = e.OldValues,
        NewValues = e.NewValues,
        IpAddress = string.IsNullOrEmpty(who.IpAddress) ? null : who.IpAddress,
        OccurredOn = DateTime.UtcNow,
    };
}
