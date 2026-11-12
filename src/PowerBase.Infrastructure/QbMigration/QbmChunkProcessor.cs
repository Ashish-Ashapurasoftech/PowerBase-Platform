using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Everything a worker needs to load chunks of one table, built once per table.</summary>
public sealed record QbmTableContext(
    long TenantId, long RunId, long TableMapId, string QbTableId, AppTable PbTable, IReadOnlyList<AppField> PbFields,
    IReadOnlyList<QbFieldMapping> Mappings, IReadOnlyDictionary<int, QbmPowerBaseReader.PbReference> ReferencesByKey,
    QbTransformOptions Options, int Tier = 0)
{
    /// <summary>The QuickBase fields a query asks for: system fields, stored fields and address parts.</summary>
    public IReadOnlyList<int> SelectFids => Mappings
        .Where(m => m.Action is QbFieldAction.System or QbFieldAction.Load or QbFieldAction.AddressPart)
        .Select(m => m.Qb.Id).Distinct().OrderBy(f => f).ToList();
}

/// <summary>Loads one chunk end to end: read every QuickBase record of the range, resolve users and parent keys for the
/// chunk, transform, write in one transaction. The chunk only counts as done when the write committed; any failure
/// leaves it not done so the queue delivers it again (the write is replay-safe).</summary>
public sealed class QbmChunkProcessor(
    IQuickBaseClient client, QbmChunkLoader loader, QbmStateStore store, QbmPowerBaseReader pb, ILogger<QbmChunkProcessor> logger)
{
    public enum Outcome { Loaded, AlreadyDone, Split }

    public async Task<Outcome> ProcessAsync(QbmTableContext ctx, long chunkId, QbRange range, CancellationToken ct = default)
    {
        if (!await store.StartChunkAsync(chunkId, ct)) return Outcome.AlreadyDone;
        try
        {
            IReadOnlyList<QbRow> rows;
            try
            {
                rows = await new QbChunkReader(client).ReadAsync(ctx.QbTableId, ctx.SelectFids, range, ct);
            }
            catch (QuickBaseTimeoutException ex)
            {
                // QuickBase's advice for a query that keeps timing out: make it smaller.
                if (await store.SplitChunkAsync(chunkId, ct))
                {
                    logger.LogWarning("Chunk {Chunk} ({From}..{To}) timed out twice; split in half", chunkId, range.From, range.To - 1);
                    return Outcome.Split;
                }
                throw new InvalidOperationException($"Chunk {chunkId} timed out and can't be split further.", ex);
            }

            var lookups = await new QbmLookupLoader(pb).LoadAsync(ctx, rows, ct);
            var transformer = new QbValueTransformer(ctx.Mappings, lookups, ctx.Options, ctx.ReferencesByKey.Keys.ToList());
            var issues = new List<QbIssue>();
            var transformed = rows.Select(r => transformer.Transform(r, issues)).ToList();

            var result = await loader.LoadAsync(ctx.TenantId, ctx.PbTable, ctx.PbFields, chunkId, range, transformed, ct);
            await store.AddIssuesAsync(ctx.RunId, ctx.TableMapId, chunkId, issues, ct);
            logger.LogInformation("Chunk {Chunk} ({From}..{To}) loaded {Rows} rows, {Issues} issues",
                chunkId, range.From, range.To - 1, result.RowsWritten, issues.Count);
            return Outcome.Loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await store.FailChunkAsync(chunkId, ex.Message, CancellationToken.None);
            throw;
        }
    }
}
