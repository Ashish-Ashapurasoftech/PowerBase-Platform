using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.SnapshotJobs;

public sealed record FieldSnapshotChunkResult(int Processed, int Changed, long LastId);

/// <summary>
/// Re-captures a Snapshot field's source value for one chunk of records of a <see cref="FieldSnapshotJob"/>. The worker
/// calls it in a loop, each time in a fresh DI scope with the requesting user's identity, until a chunk finds no more
/// records. A record whose captured value already equals the stored one is left untouched; each record that does change
/// is written and audited individually (old → new), like any other field write.
/// </summary>
public class FieldSnapshotJobProcessor
{
    /// <summary>Records per chunk: read, captured and written in one short transaction.</summary>
    public const int ChunkSize = 200;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly ISnapshotCaptureService _capture;
    private readonly ITenantUnitOfWork _uow;
    private readonly IAuditRepository _auditRepo;
    private readonly IMessagePublisher _messagePublisher;

    public FieldSnapshotJobProcessor(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        ISnapshotCaptureService capture, ITenantUnitOfWork uow, IAuditRepository auditRepo, IMessagePublisher messagePublisher)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _capture = capture;
        _uow = uow;
        _auditRepo = auditRepo;
        _messagePublisher = messagePublisher;
    }

    /// <param name="afterId">Internal Id of the last record the previous chunk handled (0 for the first).</param>
    public async Task<FieldSnapshotChunkResult> ProcessNextChunkAsync(FieldSnapshotJob job, long afterId, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(job.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var field = fields.FirstOrDefault(f => f.PublicId == job.FieldPublicId && !f.IsDeleted)
                    ?? throw new NotFoundException("Field", job.FieldPublicId);
        if (!field.SnapshotSourceFid.HasValue || !field.Fid.HasValue)
            throw new BadRequestException("Snapshot is no longer set up for this field.");

        var ids = await _recordRepo.ListIdsAfterIdAsync(table, fields, afterId, ChunkSize, null, null, ct);
        if (ids.Count == 0) return new FieldSnapshotChunkResult(0, 0, afterId);

        var rows = await _recordRepo.GetRowsByIdsAsync(table, fields, ids.Select(i => i.Id).ToList(), ct);
        var present = ids.Where(i => rows.ContainsKey(i.Id)).ToList();
        var values = await _capture.CaptureForRowsAsync(table, fields, field, present.Select(i => rows[i.Id]).ToList(), ct);

        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var label = field.Label ?? field.Name;
        var changed = 0;
        var indexMessages = new List<SearchIndexMessage>();

        await _uow.BeginAsync(ct);
        try
        {
            for (var i = 0; i < present.Count; i++)
            {
                rows[present[i].Id].TryGetValue(column, out var stored);
                if (AreEqual(stored, values[i], field.TypeCode)) continue;

                await _recordRepo.UpdateAsync(
                    table, fields, present[i].PublicId, new Dictionary<long, object?> { [field.Fid.Value] = values[i] },
                    _uow.Transaction, ct, indexMessages.Add);
                changed++;

                if (field.IsAuditable)
                    await _auditRepo.LogActivityAsync(
                        AuditActions.Updated, AuditEntityTypes.Record, present[i].PublicId.ToString(),
                        $"Snapshot field '{label}' reinitialized in {table.Name}", appId: table.AppId,
                        oldValues: JsonSerializer.Serialize(new Dictionary<string, string?> { [label] = Text(stored) }),
                        newValues: JsonSerializer.Serialize(new Dictionary<string, string?> { [label] = Text(values[i]) }),
                        ct: ct);
            }
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }

        foreach (var message in indexMessages)
            _ = _messagePublisher.PublishAsync(message, default);

        return new FieldSnapshotChunkResult(ids.Count, changed, ids[^1].Id);
    }

    /// <summary>One audit entry for the whole job: who ran it, when, and how many records were affected.</summary>
    public async Task LogSummaryAsync(FieldSnapshotJob job, int processed, int changed, string status, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(job.TablePublicId, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppField, job.FieldPublicId.ToString(),
            $"Snapshot field '{job.FieldLabel}' reinitialized in {table.Name}: {processed:N0} record(s) read, {changed:N0} changed ({status})",
            appId: table.AppId, ct: ct);
    }

    private static string? Text(object? v) => v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static bool AreEqual(object? a, object? b, string typeCode)
    {
        var x = a is DBNull ? null : a;
        var y = b is DBNull ? null : b;
        if (x is null && y is null) return true;
        if (x is null || y is null) return string.IsNullOrEmpty(Text(x ?? y));
        if (typeCode is "Number" or "Currency" or "Percent")
            return decimal.TryParse(Text(x), NumberStyles.Any, CultureInfo.InvariantCulture, out var dx)
                   && decimal.TryParse(Text(y), NumberStyles.Any, CultureInfo.InvariantCulture, out var dy) && dx == dy;
        if (typeCode is "Date" or "DateTime" or "Time")
            return DateTime.TryParse(Text(x), CultureInfo.InvariantCulture, DateTimeStyles.None, out var tx)
                   && DateTime.TryParse(Text(y), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ty) && tx == ty;
        if (typeCode == "Boolean")
            return bool.TryParse(Text(x), out var bx) && bool.TryParse(Text(y), out var by) && bx == by;
        return string.Equals(Text(x)?.Trim(), Text(y)?.Trim(), StringComparison.Ordinal);
    }
}
