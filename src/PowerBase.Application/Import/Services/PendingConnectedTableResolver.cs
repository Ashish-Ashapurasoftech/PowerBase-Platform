using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Import.Pbl;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Retries every connected table <see cref="PblConnectedTablesApplier"/> could not complete (its source application, or
/// its source table, did not exist yet) — the dependency it was waiting on may exist now, whether this import created it
/// or it was already there. Runs after every "create app from QBL" import, single or batch; never fails that import.
/// A row whose source application name now matches more than one application is left pending: ambiguity is never guessed.
/// </summary>
public class PendingConnectedTableResolver(
    IConnectedPendingImportRepository pendingRepo,
    IExistingTableConnector connectHandler,
    IAppRepository appRepo,
    IAppTableRepository tableRepo)
{
    public virtual async Task ResolveAllAsync(CancellationToken ct)
    {
        var pending = await pendingRepo.ListPendingAsync(ct);
        if (pending.Count == 0) return;

        var appsByName = PblConnectedTablesApplier.GroupByNormalizedName((await appRepo.ListAllAsync(ct)).Select(a => (a.Id, a.PublicId, a.Name)));
        var tablesCache = new Dictionary<Guid, Dictionary<string, AppTable>>();

        async Task<Dictionary<string, AppTable>> TablesOf(Guid appPublicId)
        {
            if (!tablesCache.TryGetValue(appPublicId, out var map))
            {
                var appId = await appRepo.GetIdByPublicIdAsync(appPublicId, ct);
                map = (await tableRepo.ListByAppAsync(appId, ct)).GroupBy(t => PblConnectedTablesApplier.Normalize(t.Name)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                tablesCache[appPublicId] = map;
            }
            return map;
        }

        foreach (var item in pending)
        {
            try
            {
                var connected = JsonSerializer.Deserialize<PblConnectedTable>(item.PayloadJson)
                    ?? throw new InvalidOperationException("The saved connected-table definition could not be read.");
                // The row's own columns are what was saved; the payload carries the Quickbase table id (rows saved before it existed have none).
                connected.SourceAppName = item.SourceAppName;
                connected.SourceTableName = item.SourceTableName;

                var source = await PblConnectedTablesApplier.LocateSourceAsync(connected, item.AppTableId, tableRepo, appsByName, TablesOf, null, ct);
                if (source.Outcome == PblConnectedTablesApplier.SourceOutcome.Ambiguous)
                {
                    var message = source.Message.Length > 0 ? char.ToUpperInvariant(source.Message[0]) + source.Message[1..] : source.Message;
                    await pendingRepo.RecordAttemptAsync(item.Id, $"{message}; connect it to its source by hand.", ct);
                    continue;
                }
                if (source.Outcome == PblConnectedTablesApplier.SourceOutcome.AppNotFound)
                {
                    await pendingRepo.RecordAttemptAsync(item.Id, $"'{item.SourceAppName}' has not been imported yet.", ct);
                    continue;
                }
                if (source.Outcome == PblConnectedTablesApplier.SourceOutcome.TableNotFound)
                {
                    await pendingRepo.RecordAttemptAsync(item.Id, $"'{item.SourceTableName}' was not found in '{source.AppName}' yet.", ct);
                    continue;
                }

                var destination = await tableRepo.GetByIdAsync(item.AppTableId, ct);
                var result = await connectHandler.ConnectAsync(
                    PblConnectedTablesApplier.BuildCommand(destination.PublicId, source.AppPublicId, source.TablePublicId, connected), ct);

                await pendingRepo.MarkResolvedAsync(item.Id, result.ConnectedTablePublicId, ct);
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException or JsonException)
            {
                await pendingRepo.RecordAttemptAsync(item.Id, $"Not set up yet ({ex.Message}).", ct);
            }
        }
    }
}
