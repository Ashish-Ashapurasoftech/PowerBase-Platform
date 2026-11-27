using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Quickbase connected tables → PowerBase connected tables. A connected table's source application is looked up
/// against every application the tenant already has — the ones just imported in this call included, since by the time
/// this runs they are already persisted — never only the documents passed in. A source that still can't be found (or
/// whose source table isn't there yet) is saved to <see cref="IConnectedPendingImportRepository"/> instead of being
/// lost, so <see cref="PendingConnectedTableResolver"/> can complete it once that dependency is imported. A table whose
/// source application name matches more than one existing app is left alone — ambiguity is never guessed. Failures are
/// reported per table and never fail the import.
/// </summary>
public class PblConnectedTablesApplier(
    IExistingTableConnector connectHandler,
    IAppRepository appRepo,
    IAppTableRepository tableRepo,
    IConnectedPendingImportRepository pendingRepo)
{
    public virtual async Task ApplyAsync(IReadOnlyList<(PblDocument Document, ImportReport Report)> imported, CancellationToken ct)
    {
        if (imported.All(i => (i.Document.ConnectedTables ?? []).Count == 0)) return;

        var appsByName = GroupByNormalizedName((await appRepo.ListAllAsync(ct)).Select(a => (a.Id, a.PublicId, a.Name)));
        var tablesCache = new Dictionary<Guid, Dictionary<string, Domain.Entities.AppTable>>();

        async Task<Dictionary<string, Domain.Entities.AppTable>> TablesOf(Guid appPublicId)
        {
            if (!tablesCache.TryGetValue(appPublicId, out var map))
            {
                var appId = await appRepo.GetIdByPublicIdAsync(appPublicId, ct);
                map = (await tableRepo.ListByAppAsync(appId, ct)).GroupBy(t => Normalize(t.Name)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                tablesCache[appPublicId] = map;
            }
            return map;
        }

        // Apps created by this import: when a Quickbase app was imported more than once, its copy from this import is the source.
        var batchApps = imported.Select(i => i.Report.AppPublicId).ToHashSet();

        foreach (var (document, report) in imported)
        {
            foreach (var connected in document.ConnectedTables ?? [])
            {
                void Note(string reason) => report.Skipped.Add(new ImportSkippedItem { LogicalRef = connected.TableRef, Name = connected.TableName, Reason = reason });
                try
                {
                    // Looked up opportunistically: a deferred row can only be saved against a destination table that
                    // exists, but a missing source is reported either way (the table's own mapping may have excluded
                    // the destination, which is independent of whether its source is known).
                    var ownTables = await TablesOf(report.AppPublicId);
                    var hasDestination = ownTables.TryGetValue(Normalize(connected.TableName), out var destination);

                    var source = await LocateSourceAsync(connected, destination?.Id ?? 0, tableRepo, appsByName, TablesOf, batchApps, ct);
                    if (source.Outcome == SourceOutcome.Ambiguous)
                    {
                        Note($"Connected table: {source.Message}; connect it to its source by hand.");
                        continue;
                    }
                    if (source.Outcome == SourceOutcome.AppNotFound)
                    {
                        var reason = $"Connected table: its records come from '{connected.SourceTableName}' in '{connected.SourceAppName}', which does not exist yet. " +
                                     "The table was imported as an ordinary table; it will connect automatically once that application is imported.";
                        Note(reason);
                        if (hasDestination) await SavePendingAsync(destination!, connected, reason, ct);
                        continue;
                    }

                    if (!hasDestination)
                        continue; // the table itself was excluded from this import; nothing to connect

                    if (source.Outcome == SourceOutcome.TableNotFound)
                    {
                        var reason = $"Connected table: '{connected.SourceTableName}' was not found in '{source.AppName}' yet. " +
                                     "The table was imported as an ordinary table; it will connect automatically once that table exists.";
                        Note(reason);
                        await SavePendingAsync(destination!, connected, reason, ct);
                        continue;
                    }

                    var result = await connectHandler.ConnectAsync(BuildCommand(destination!.PublicId, source.AppPublicId, source.TablePublicId, connected), ct);

                    foreach (var note in result.Notes) Note($"Connected table: {note}");
                }
                catch (Exception ex) when (ex is DomainException or InvalidOperationException)
                {
                    Note($"Connected table to '{connected.SourceTableName}' in '{connected.SourceAppName}' was not set up ({ex.Message}); the table stays an ordinary table.");
                }
            }
        }
    }

    private async Task SavePendingAsync(Domain.Entities.AppTable destination, PblConnectedTable connected, string reason, CancellationToken ct)
    {
        try
        {
            await pendingRepo.UpsertAsync(new ConnectedPendingImportConnection
            {
                AppId = destination.AppId,
                AppTableId = destination.Id,
                SourceAppName = connected.SourceAppName,
                SourceTableName = connected.SourceTableName,
                PayloadJson = JsonSerializer.Serialize(connected),
                Reason = Truncate(reason, 500),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the report already told the user it stayed an ordinary table. Losing the retry
            // is better than failing an otherwise-successful import over bookkeeping.
        }
    }

    internal static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    public enum AppMatchOutcome { NotFound, Found, Ambiguous }

    public readonly record struct AppMatch(AppMatchOutcome Outcome, long Id, Guid PublicId, string Name, int Count)
    {
        public static readonly AppMatch None = new(AppMatchOutcome.NotFound, 0, Guid.Empty, "", 0);
    }

    /// <summary>Groups candidate applications by their normalized name — the only key QBL's connected-table data source
    /// gives us (see <see cref="PowerBase.Application.Import.Qbl.QblToPblConverter"/>, which derives it from the
    /// connection's own reference slug, not a stable application id).</summary>
    public static Dictionary<string, List<(long Id, Guid PublicId, string Name)>> GroupByNormalizedName(IEnumerable<(long Id, Guid PublicId, string Name)> apps)
    {
        var map = new Dictionary<string, List<(long Id, Guid PublicId, string Name)>>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            var key = Normalize(app.Name);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(app);
        }
        return map;
    }

    /// <summary>Exactly one normalized-name match → <see cref="AppMatchOutcome.Found"/>; none → <see cref="AppMatchOutcome.NotFound"/>;
    /// more than one → <see cref="AppMatchOutcome.Ambiguous"/> (never guessed).</summary>
    public static AppMatch ResolveSourceApp(string sourceAppName, IReadOnlyDictionary<string, List<(long Id, Guid PublicId, string Name)>> appsByNormalizedName)
    {
        if (!appsByNormalizedName.TryGetValue(Normalize(sourceAppName), out var matches) || matches.Count == 0)
            return AppMatch.None;
        if (matches.Count > 1)
            return new AppMatch(AppMatchOutcome.Ambiguous, 0, Guid.Empty, "", matches.Count);
        var (id, publicId, name) = matches[0];
        return new AppMatch(AppMatchOutcome.Found, id, publicId, name, 1);
    }

    public enum SourceOutcome { Found, AppNotFound, TableNotFound, Ambiguous }

    /// <summary>Where a connected table's records come from. <see cref="Message"/> explains an ambiguous result.</summary>
    public readonly record struct SourceLocation(SourceOutcome Outcome, Guid AppPublicId, string AppName, Guid TablePublicId, string Message);

    /// <summary>
    /// Finds a connected table's source table. First by its Quickbase table id (<see cref="PblConnectedTable.SourceQbTableId"/>
    /// = <c>AppTable.QBTableID</c>), which is exact whatever the apps are called; then, when no table carries that id, by the
    /// data source's name and the source table's name (the older, less reliable way). Several tables with the same Quickbase id
    /// (the app was imported more than once) are narrowed to the copy in <paramref name="preferredApps"/>, then to the copy
    /// whose app name matches the data source; still more than one is <see cref="SourceOutcome.Ambiguous"/>, never guessed.
    /// Shared by <see cref="PendingConnectedTableResolver"/> so a deferred connection is completed the same way.
    /// </summary>
    public static async Task<SourceLocation> LocateSourceAsync(
        PblConnectedTable connected, long destinationTableId, IAppTableRepository tableRepo,
        IReadOnlyDictionary<string, List<(long Id, Guid PublicId, string Name)>> appsByName,
        Func<Guid, Task<Dictionary<string, Domain.Entities.AppTable>>> tablesOf,
        IReadOnlySet<Guid>? preferredApps, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connected.SourceQbTableId))
        {
            IReadOnlyList<QbSourceTableMatch> byId;
            try { byId = await tableRepo.ListByQbTableIdAsync(connected.SourceQbTableId, ct) ?? []; }
            catch (Exception ex) when (ex is not OperationCanceledException) { byId = []; } // e.g. QBTableID not migrated yet: use names

            var candidates = byId.Where(m => m.TableId != destinationTableId).ToList();
            if (candidates.Count > 1 && preferredApps is { Count: > 0 })
            {
                var inBatch = candidates.Where(m => preferredApps.Contains(m.AppPublicId)).ToList();
                if (inBatch.Count > 0) candidates = inBatch;
            }
            if (candidates.Count > 1)
            {
                var sameName = candidates.Where(m => Normalize(m.AppName) == Normalize(connected.SourceAppName)).ToList();
                if (sameName.Count > 0) candidates = sameName;
            }
            if (candidates.Count == 1)
                return new SourceLocation(SourceOutcome.Found, candidates[0].AppPublicId, candidates[0].AppName, candidates[0].TablePublicId, "");
            if (candidates.Count > 1)
                return new SourceLocation(SourceOutcome.Ambiguous, Guid.Empty, "", Guid.Empty,
                    $"its source table '{connected.SourceTableName}' (Quickbase id {connected.SourceQbTableId}) exists in more than one application " +
                    $"({string.Join(", ", candidates.Select(c => $"'{c.AppName}'").Distinct())})");
        }

        var match = ResolveSourceApp(connected.SourceAppName, appsByName);
        if (match.Outcome == AppMatchOutcome.Ambiguous)
            return new SourceLocation(SourceOutcome.Ambiguous, Guid.Empty, "", Guid.Empty,
                $"more than one application is named '{connected.SourceAppName}' ({match.Count} matches)");
        if (match.Outcome == AppMatchOutcome.NotFound)
            return new SourceLocation(SourceOutcome.AppNotFound, Guid.Empty, "", Guid.Empty, "");

        var sourceTables = await tablesOf(match.PublicId);
        return sourceTables.TryGetValue(Normalize(connected.SourceTableName), out var sourceTable)
            ? new SourceLocation(SourceOutcome.Found, match.PublicId, match.Name, sourceTable.PublicId, "")
            : new SourceLocation(SourceOutcome.TableNotFound, match.PublicId, match.Name, Guid.Empty, "");
    }

    /// <summary>Public so <see cref="PendingConnectedTableResolver"/> builds the same command when it completes a deferred connection.</summary>
    public static ConnectExistingTableCommand BuildCommand(Guid destinationTablePublicId, Guid sourceAppPublicId, Guid sourceTablePublicId, PblConnectedTable connected) =>
        new(destinationTablePublicId, sourceAppPublicId, sourceTablePublicId,
            connected.Fields.Where(f => f.SourceFid.HasValue).Select(f => new ConnectExistingFieldMapping(f.FieldName, f.SourceFid!.Value)).ToList(),
            string.Equals(connected.UpdateMode, "Destructive", StringComparison.OrdinalIgnoreCase) ? ConnectedUpdateModes.Mirror : ConnectedUpdateModes.Append,
            ToSchedule(connected.CronTab), connected.NotifyOnFailure, ToFilter(connected));

    /// <summary>Quickbase cron (minute hour day-of-month month day-of-week) → the module's schedule. A list of hours is an hourly window
    /// from the first to the last hour; the minute is chosen by the server.</summary>
    public static ConnectedSchedule? ToSchedule(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;

        var hours = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => int.TryParse(h, out var n) ? n : -1).Where(n => n is >= 0 and <= 23).ToList();
        if (hours.Count == 0) return null;

        if (parts[2] != "*" && int.TryParse(parts[2], out var day))
            return new ConnectedSchedule { Type = ConnectedScheduleTypes.Monthly, Day = Math.Clamp(day, 1, 31), Hour = hours[0] };
        if (parts[4] != "*" && int.TryParse(parts[4], out var weekday))
            return new ConnectedSchedule { Type = ConnectedScheduleTypes.Weekly, Weekday = weekday % 7, Hour = hours[0] };
        if (hours.Count == 1)
            return new ConnectedSchedule { Type = ConnectedScheduleTypes.Daily, Hour = hours[0] };
        return new ConnectedSchedule { Type = ConnectedScheduleTypes.Hourly, Hour = hours.Min(), EndHour = hours.Max() };
    }

    /// <summary>Public so <see cref="PendingConnectedTableResolver"/> can reuse it when completing a deferred connection.</summary>
    public static FilterGroup? ToFilter(PblConnectedTable connected)
    {
        var conditions = connected.FilterConditions.Where(c => c.SourceFid.HasValue).ToList();
        if (conditions.Count == 0) return null;
        return new FilterGroup
        {
            Logic = connected.FilterLogic == "any" ? "or" : "and",
            Nodes = conditions.Select(c => new FilterNode { Condition = new FilterCondition { FieldId = c.SourceFid!.Value, Operator = c.Operator, Value = c.Value } }).ToList(),
        };
    }
}
