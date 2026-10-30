using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Search;

/// <summary>
/// Removes a deleted field's values from the index. The field is gone from the table by the time the indexer runs, so the message carries
/// the exact index columns the field used; the indexer pages through the table's records and writes null into them.
/// </summary>
public static class FieldDeleteIndexCleanup
{
    /// <summary>Call BEFORE deleting: the columns are read from the fields as they are now. Fields that were never indexed give no message.</summary>
    public static IReadOnlyList<SearchIndexMessage> Capture(long tenantId, AppTable table, IReadOnlyList<AppField> allFields, IEnumerable<AppField> deleting)
    {
        var info = SearchTableSlotInfo.From(table.Id, allFields);
        var messages = new List<SearchIndexMessage>();
        foreach (var field in deleting)
        {
            if (!field.Fid.HasValue || !info.StoredSlots.TryGetValue(field.Fid.Value, out var slot)) continue;
            messages.Add(new SearchIndexMessage
            {
                Action = IndexAction.NullifyField,
                TenantId = tenantId,
                AppId = table.AppId,
                TableId = table.Id,
                FieldId = field.Fid.Value,
                Page = 1,
                AfterId = 0,
                Slot = slot.Slot,
                EndSlot = slot.EndSlot
            });
        }
        return messages;
    }

    /// <summary>Call AFTER the delete succeeded. Never fails the delete. Each job is recorded first (meta.SearchFieldJob), so a send that is
    /// lost even after a few retries is re-sent by the search relay; the publisher logs each failure. Sent even if the request is cancelled
    /// now: the delete has happened.</summary>
    public static async Task PublishAsync(IMessagePublisher? publisher, IReadOnlyList<SearchIndexMessage> messages, CancellationToken ct, ISearchFieldJobs? jobs = null)
    {
        if (publisher == null || messages.Count == 0) return;
        foreach (var message in messages)
            message.JobId ??= await FieldSlotSync.RecordJobAsync(jobs, message, null);
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await publisher.PublishBatchAsync(messages, CancellationToken.None);
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken.None);
            }
            catch (Exception) { /* logged by the publisher */ }
        }
    }
}

/// <summary>Re-pushes one field's values to the index after something changed them without going through a record write (a column conversion).</summary>
public static class SearchFieldReindex
{
    /// <summary>Recorded first (meta.SearchFieldJob) and sent with retries: a send that is still lost is re-sent by the search relay.</summary>
    public static async Task RequestAsync(IMessagePublisher? publisher, IQueryContext? queryContext, AppTable table, AppField field, CancellationToken ct,
        ISearchFieldJobs? jobs = null)
    {
        if (publisher == null || queryContext == null || !field.Fid.HasValue) return;
        if (!(field.IsSearchable || field.IsFilterable)) return;
        var message = FieldSlotSync.Message(queryContext.TenantId, table.AppId, table.Id, field.Fid.Value, isNullify: false, clearSlot: null, afterId: 0);
        message.JobId = await FieldSlotSync.RecordJobAsync(jobs, message, null);
        await FieldSlotSync.PublishAsync(publisher, message, null, CancellationToken.None);
    }
}
