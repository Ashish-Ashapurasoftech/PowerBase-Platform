using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The "Metadata" Quickbase shows under a record trigger ({{metadata.a.context.*}}, {{metadata.a.record.action}},
/// {{metadata.a.struct.&lt;field&gt;.qb_field_id|qb_field_type}}). PowerFlows reads it as {{steps.a._metadata.*}}.
/// </summary>
public partial class PipelineEngine
{
    public static string? QuickbaseRecordAction(string? eventType) => eventType switch
    {
        "Added" => "Add",
        "Modified" => "Replace",
        "Deleted" => "Delete",
        _ => null
    };

    private async Task<Dictionary<string, object?>?> BuildRecordTriggerMetadataAsync(Dictionary<string, object> triggerData, CancellationToken ct)
    {
        try
        {
            string? Text(string key) => triggerData.TryGetValue(key, out var value) && value != null
                ? (value is JsonElement element ? (element.ValueKind == JsonValueKind.String ? element.GetString() : element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.GetRawText()) : value.ToString())
                : null;

            var context = new Dictionary<string, object?>
            {
                ["app_name"] = null, ["app_id"] = null, ["table_name"] = null, ["table_id"] = null,
                ["realm_host"] = null, ["realm_id"] = null, ["occurred_at"] = null, ["user"] = null
            };
            var schema = new Dictionary<string, object?>();

            if (Guid.TryParse(Text("TablePublicId"), out var tablePublicId))
            {
                var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
                context["table_name"] = table.Name;
                context["table_id"] = table.PublicId.ToString();
                if (_serviceProvider.GetService<IAppRepository>() is { } apps)
                {
                    var app = await apps.GetByIdAsync(table.AppId, ct);
                    context["app_name"] = app.Name;
                    context["app_id"] = app.PublicId.ToString();
                }
                foreach (var field in await _fieldRepo.ListByTableAsync(table.Id, ct))
                {
                    if (!field.Fid.HasValue) continue;
                    schema[$"fid_{field.Fid.Value}"] = new Dictionary<string, object?>
                    {
                        ["qb_field_id"] = field.Fid.Value,
                        ["qb_field_type"] = field.TypeCode
                    };
                }
            }
            else if (Guid.TryParse(Text("AppPublicId"), out var appPublicId) && _serviceProvider.GetService<IAppRepository>() is { } apps)
            {
                var app = await apps.GetByPublicIdAsync(appPublicId, ct);
                context["app_name"] = app.Name;
                context["app_id"] = app.PublicId.ToString();
            }

            var baseUrl = _serviceProvider.GetService<IConfiguration>()?["Frontend:BaseUrl"];
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) context["realm_host"] = baseUri.Host;
            try { context["realm_id"] = (await _tenantRepo.GetByIdAsync(_queryContext.TenantId, ct)).PublicId.ToString(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* realm id stays empty */ }

            // Quickbase reports the time of the change with minute precision.
            if (DateTime.TryParse(Text("EventTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var occurred))
                context["occurred_at"] = new DateTime(occurred.Year, occurred.Month, occurred.Day, occurred.Hour, occurred.Minute, 0, DateTimeKind.Utc);

            if (long.TryParse(Text("TriggeredBy"), out var userId) && userId > 0
                && _serviceProvider.GetService<IUserRepository>() is { } users)
            {
                try { context["user"] = (await users.GetByIdAsync(userId, ct)).Email; }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* user stays empty */ }
            }

            var record = new Dictionary<string, object?> { ["action"] = QuickbaseRecordAction(Text("EventType")) };
            // powerflowextraRd: Quickbase's Record → Previous record ({{metadata.a.record.previous.name}}, docs: {{a.$prev.name}}) —
            // the record as it was before the change, "available only if action is Replace". PowerFlows keys it by field number,
            // like the trigger's own values: {{steps.a._metadata.record.previous.fid_N}} (a user field also .fid_N.email …).
            // Every field of the table is present (empty unless the record was replaced), so a token reading the previous value
            // resolves to empty on an added / deleted record, as in Quickbase, instead of failing the step.
            var previous = schema.Keys.ToDictionary(key => key, _ => (object?)null, StringComparer.Ordinal);
            if (record["action"] is "Replace" && triggerData.TryGetValue("OldValues", out var oldValues)
                && oldValues is JsonElement { ValueKind: JsonValueKind.Object } element)
            {
                foreach (var property in element.EnumerateObject())
                    previous[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
            }
            record["previous"] = previous;

            return new Dictionary<string, object?>
            {
                ["context"] = context,
                ["record"] = record,
                ["struct"] = schema
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not build the record trigger metadata.");
            return null;
        }
    }
}

public partial class PipelineEngine
{
    /// <summary>Search Records' Metadata. loop.index is filled per item while a loop runs over the search.</summary>
    public static Dictionary<string, object?> SearchMetadata(int size) => new()
    {
        ["batch"] = new Dictionary<string, object?> { ["size"] = size },
        ["loop"] = new Dictionary<string, object?> { ["index"] = null }
    };
}
