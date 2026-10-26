using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Quickbase connected tables → PowerBase connected tables, once every application of the file exists. A connected table whose source
/// application is one of the imported apps (same realm) is converted in place: the table the import already made becomes the connected
/// table, keeping its fields, forms and reports. A table whose source application is not in the file stays an ordinary table, and the
/// report says so. Failures are reported per table and never fail the import.
/// </summary>
public class PblConnectedTablesApplier(
    ConnectExistingTableHandler connectHandler,
    IAppRepository appRepo,
    IAppTableRepository tableRepo)
{
    public virtual async Task ApplyAsync(IReadOnlyList<(PblDocument Document, ImportReport Report)> imported, CancellationToken ct)
    {
        if (imported.All(i => (i.Document.ConnectedTables ?? []).Count == 0)) return;

        var appByName = imported.ToDictionary(i => Normalize(i.Report.AppName), i => i.Report, StringComparer.Ordinal);
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

        foreach (var (document, report) in imported)
        {
            foreach (var connected in document.ConnectedTables ?? [])
            {
                void Note(string reason) => report.Skipped.Add(new ImportSkippedItem { LogicalRef = connected.TableRef, Name = connected.TableName, Reason = reason });
                try
                {
                    if (!appByName.TryGetValue(Normalize(connected.SourceAppName), out var sourceReport))
                    {
                        Note($"Connected table: its records come from '{connected.SourceTableName}' in '{connected.SourceAppName}', which is not one of the imported apps. The table was imported as an ordinary table; connect it to its source by hand.");
                        continue;
                    }
                    var sourceTables = await TablesOf(sourceReport.AppPublicId);
                    if (!sourceTables.TryGetValue(Normalize(connected.SourceTableName), out var sourceTable))
                    {
                        Note($"Connected table: '{connected.SourceTableName}' was not found in '{sourceReport.AppName}'. The table was imported as an ordinary table.");
                        continue;
                    }
                    var ownTables = await TablesOf(report.AppPublicId);
                    if (!ownTables.TryGetValue(Normalize(connected.TableName), out var destination))
                        continue;

                    var result = await connectHandler.ConnectAsync(new ConnectExistingTableCommand(
                        destination.PublicId, sourceReport.AppPublicId, sourceTable.PublicId,
                        connected.Fields.Where(f => f.SourceFid.HasValue).Select(f => new ConnectExistingFieldMapping(f.FieldName, f.SourceFid!.Value)).ToList(),
                        string.Equals(connected.UpdateMode, "Destructive", StringComparison.OrdinalIgnoreCase) ? ConnectedUpdateModes.Mirror : ConnectedUpdateModes.Append,
                        ToSchedule(connected.CronTab), connected.NotifyOnFailure, ToFilter(connected)), ct);

                    foreach (var note in result.Notes) Note($"Connected table: {note}");
                }
                catch (Exception ex) when (ex is DomainException or InvalidOperationException)
                {
                    Note($"Connected table to '{connected.SourceTableName}' in '{connected.SourceAppName}' was not set up ({ex.Message}); the table stays an ordinary table.");
                }
            }
        }
    }

    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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

    private static FilterGroup? ToFilter(PblConnectedTable connected)
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
