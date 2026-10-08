using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Brings the computed (Formula / Lookup / Summary) values of an On New Event trigger record up to date when
/// the PowerFlow starts running.
///
/// The event payload is captured inside the write that fired it, so a computed value in it only knows what
/// existed at that instant. The common case: an Order is saved together with its Order Detail rows — the
/// Order's "Sum of Amount" Summary is blank in the payload because the details were written a moment
/// later, although the PowerFlow runs after they exist. A Condition on <c>Sum of Amount &gt; 100</c> then read a
/// blank and took the "not met" branch. The run reads them again here, outside any write transaction, and
/// overlays only the computed fields that are already part of the trigger's field values; stored fields
/// keep the values of the event, and nothing is added that the event did not already expose.
/// </summary>
public static class PipelineTriggerRefresh
{
    /// <summary>How long the re-read may take before the event's own values are used as they are.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static readonly string[] ValueSections = ["SelectedFieldValues", "NewValues"];

    private static bool IsRefreshable(AppField f) =>
        f.Fid.HasValue && !f.IsDeleted && (f.TypeCode is "Lookup" or "Summary" || FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings));

    /// <returns>true when at least one value was replaced.</returns>
    public static async Task<bool> RefreshAsync(
        Dictionary<string, object> triggerData,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationalProjector? relationalProjector,
        IFormulaProjector? formulaProjector,
        ILogger? logger,
        CancellationToken ct)
    {
        if (formulaProjector == null) return false;

        // Only a record that still exists and belongs to this account: added / modified, same tenant.
        var eventType = Text(triggerData, "EventType");
        if (!string.Equals(eventType, "Added", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(eventType, "Modified", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(Text(triggerData, "ConnectionPublicId"))) return false;
        if (!Guid.TryParse(Text(triggerData, "TablePublicId"), out var tableId) ||
            !Guid.TryParse(Text(triggerData, "RecordPublicId"), out var recordId)) return false;

        var sections = ValueSections
            .Where(name => triggerData.TryGetValue(name, out var v) && v is JsonElement { ValueKind: JsonValueKind.Object })
            .ToDictionary(name => name, name => (JsonElement)triggerData[name]);
        if (sections.Count == 0) return false;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            var table = await tableRepo.GetByPublicIdAsync(tableId, budget.Token);
            var fields = await fieldRepo.ListByTableAsync(table.Id, budget.Token);

            // The computed fields the trigger already carries a value slot for.
            var wanted = fields.Where(IsRefreshable)
                .Where(f => sections.Values.Any(s => s.TryGetProperty($"fid_{f.Fid}", out _)))
                .ToList();
            if (wanted.Count == 0) return false;

            var ids = await recordRepo.GetRecordIdsByPublicIdsAsync(table, [recordId], null, budget.Token);
            if (!ids.TryGetValue(recordId, out var rowId)) return false;
            var rows = await recordRepo.GetRowsByIdsAsync(table, fields, [rowId], budget.Token);
            if (!rows.TryGetValue(rowId, out var row)) return false;

            var computed = await PipelineComputedProjection.ProjectAsync(
                relationalProjector, formulaProjector, table, fields, [row], logger, budget.Token);
            if (computed == null || computed.Values.Count == 0) return false;

            var fresh = new Dictionary<string, object?>();
            foreach (var field in wanted)
            {
                // A field that could not be computed keeps the value the event carried.
                if (computed.FailedFids.Contains(field.Fid!.Value)) continue;
                if (computed.Values[0].TryGetValue(field.Fid.Value, out var value))
                    fresh[$"fid_{field.Fid}"] = value;
            }
            if (fresh.Count == 0) return false;

            foreach (var (name, section) in sections)
            {
                var merged = new Dictionary<string, object?>();
                foreach (var property in section.EnumerateObject()) merged[property.Name] = property.Value.Clone();
                foreach (var (key, value) in fresh)
                    if (merged.ContainsKey(key)) merged[key] = value;
                triggerData[name] = JsonSerializer.SerializeToElement(merged);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Any failure (a table this account doesn't have, a slow read, …) leaves the event's own values.
            logger?.LogWarning(ex, "Could not refresh the computed values of trigger record {RecordId}; using the values captured with the event.", recordId);
            return false;
        }
    }

    private static string? Text(Dictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var v) && v != null
            ? v is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : v is JsonElement { ValueKind: JsonValueKind.Null } ? null : v.ToString()
            : null;
}
