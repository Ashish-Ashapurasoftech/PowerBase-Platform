using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.API.Models.Records;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.ConnectedTables;

/// <summary>Layer 1 of the read-only rule (plan §8.4): refuses every HTTP write that would add, delete or change connected
/// data in a connected table, without touching the endpoints themselves. Requests to ordinary tables pass straight through
/// after one cached lookup. Writes that don't come through HTTP (PowerFlows, background imports) are undone by the next
/// refresh (layer 2).</summary>
public sealed class ConnectedTableWriteFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var template = context.ActionDescriptor.AttributeRouteInfo?.Template;
        if (template is null || !WriteMethods.Contains(request.Method) || template.StartsWith("connected", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var services = context.HttpContext.RequestServices;
        var queryContext = services.GetRequiredService<IQueryContext>();
        if (queryContext.TenantId == 0)
        {
            await next();
            return;
        }

        var message = await CheckAsync(context, template, request.Method, services, queryContext.TenantId, context.HttpContext.RequestAborted);
        if (message is null)
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(new { error = new { code = ConnectedErrorCodes.ReadOnly, message } })
        {
            StatusCode = StatusCodes.Status409Conflict
        };
    }

    /// <summary>The refusal message, or null when the write may go ahead.</summary>
    private static async Task<string?> CheckAsync(ActionExecutingContext context, string template, string method, IServiceProvider services, long tenantId, CancellationToken ct)
    {
        var repository = services.GetRequiredService<IConnectedTableRepository>();
        var t = template.ToLowerInvariant();

        // tables/{tableId}/…: records, quick imports, import files, import definitions into the table.
        if (t.StartsWith("tables/{tableid") && RouteGuid(context, "tableId") is { } tablePublicId)
        {
            var cache = await ConnectedTableCache.GetAsync(tenantId, repository, ct);
            if (!cache.PublicIds.Contains(tablePublicId)) return null;

            if (t.Contains("/records/{id:guid}/files/") || t.EndsWith("/actions/{fid:int}/check")) return null; // files are never connected; a check writes nothing
            if (method == "PATCH" && t.EndsWith("/records/{id:guid}"))
                return await TouchesConnectedFieldsAsync(context, "request", repository, tablePublicId, ct)
                    ? "Connected fields can't be edited. Their values come from the source and change only when the table refreshes."
                    : null;
            if (t.EndsWith("/records/mass-update"))
                return await TouchesConnectedFieldsAsync(context, "request", repository, tablePublicId, ct)
                    ? "Connected fields can't be edited. Their values come from the source and change only when the table refreshes."
                    : null;
            if (t.Contains("/records"))
                return "Records can't be added to or removed from a connected table by hand. They are added and removed only when the table refreshes.";
            if (t.Contains("import"))
                return "You can't import into a connected table. Its records come from the source and change only when the table refreshes.";
            return null;
        }

        // reports/{id}/records/delete-matching: deletes records of the report's table.
        if (t.StartsWith("reports/{publicid") && t.EndsWith("/records/delete-matching") && RouteGuid(context, "publicId") is { } reportId)
        {
            long tableId;
            try { tableId = (await services.GetRequiredService<IReportRepository>().GetByPublicIdAsync(reportId, ct)).AppTableId; }
            catch (NotFoundException) { return null; }
            var cache = await ConnectedTableCache.GetAsync(tenantId, repository, ct);
            return cache.Ids.Contains(tableId)
                ? "Records can't be removed from a connected table by hand. They are removed only when the table refreshes."
                : null;
        }

        // Starting a saved import whose destination is a connected table.
        Guid? definitionId = t.StartsWith("import-definitions/{id") && t.EndsWith("/runs") ? RouteGuid(context, "id")
            : t.StartsWith("apps/{appid") && t.Contains("/imports/{importid") && t.EndsWith("/run") ? RouteGuid(context, "importId")
            : null;
        if (definitionId is { } defId)
        {
            var definition = await services.GetRequiredService<IImportDefinitionRepository>().GetByPublicIdAsync(defId, ct);
            if (definition is null) return null;
            var cache = await ConnectedTableCache.GetAsync(tenantId, repository, ct);
            return cache.Ids.Contains(definition.DestinationTableId)
                ? "You can't import into a connected table. Its records come from the source and change only when the table refreshes."
                : null;
        }
        return null;
    }

    private static async Task<bool> TouchesConnectedFieldsAsync(ActionExecutingContext context, string argumentName, IConnectedTableRepository repository, Guid tablePublicId, CancellationToken ct)
    {
        Dictionary<string, JsonElement>? fields = context.ActionArguments.TryGetValue(argumentName, out var arg)
            ? arg switch
            {
                UpdateRecordRequest u => u.Fields,
                MassUpdateRecordsRequest m => m.Fields,
                _ => null,
            }
            : null;
        if (fields is null || fields.Count == 0) return false;
        var connected = (await repository.ListConnectedFidsAsync(tablePublicId, ct)).Select(f => f.ToString()).ToHashSet();
        return fields.Keys.Any(connected.Contains);
    }

    private static Guid? RouteGuid(ActionExecutingContext context, string name) =>
        context.RouteData.Values.TryGetValue(name, out var value) && Guid.TryParse(value?.ToString(), out var id) ? id : null;
}
