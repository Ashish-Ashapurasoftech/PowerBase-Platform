using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports;

/// <summary>
/// The fields a Table report shows, in order: its own explicit Columns, else the table's Default Report Settings columns
/// (when the report is in "Default" columns mode), else every reportable field. Shared by the report runner and by the
/// record picker that is based on a report, so both read a report's columns the same way.
/// </summary>
internal static class ReportColumnResolver
{
    /// <param name="visibleFieldIds">Fids the viewer's role may see; a column outside it is dropped.</param>
    public static async Task<IReadOnlyList<AppField>> ResolveAsync(
        ReportDefinition definition, IReadOnlyList<AppField> allFields, IReadOnlySet<long> visibleFieldIds,
        IReportRepository reportRepo, Guid tablePublicId, CancellationToken ct)
    {
        IReadOnlyList<AppField> selectedFields = [];
        if (definition.Columns.Count > 0)
        {
            var fieldMap = allFields
                .Where(f => f.Fid.HasValue)
                .GroupBy(f => (long)f.Fid!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            // A report's own explicit Columns are never filtered by IsReportable here — Custom
            // mode is deliberately allowed to keep or add a field that isn't reportable, and that
            // holds even when this particular report is also the table's default report: what
            // matters is THIS row's own ColumnsMode, not the IsDefault flag. (The reportable
            // restriction only applies to what OTHER reports inherit from this row when they're
            // in "Default columns" mode — see below.)
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                .Select(id => fieldMap[id])
                .ToList();
        }

        if (selectedFields.Count == 0 && definition.ColumnsMode == "Default")
        {
            var defaultSettings = await reportRepo.GetDefaultSettingsRecordAsync(tablePublicId, ct);
            if (defaultSettings is not null)
            {
                var defaultColumnIds = (JsonSerializer.Deserialize<ReportDefinition>(defaultSettings.Definition) ?? new ReportDefinition()).Columns;

                if (defaultColumnIds.Count > 0)
                {
                    var fieldMap = allFields
                        .Where(f => f.Fid.HasValue)
                        .GroupBy(f => (long)f.Fid!.Value)
                        .ToDictionary(g => g.Key, g => g.First());
                    selectedFields = defaultColumnIds
                        // Same reasoning as above: the inherited-from list can only ever be reportable
                        // fields, regardless of whatever stale ids the default report's own row still has.
                        .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id) && fieldMap[id].IsReportable)
                        .Select(id => fieldMap[id])
                        .ToList();
                }
                else
                {
                    // The default report exists but has never been explicitly configured — same
                    // "all non-system fields" inference the Default Report Settings columns picker
                    // shows before its first save (table-default-report-settings.component.ts's
                    // initState()). Without this, a report merely inheriting an *unconfigured*
                    // default fell through to the generic "every reportable field" fallback below —
                    // which also pulls in Record ID#/Date Created/etc — instead of matching what the
                    // settings page visually promises until someone actually saves it once.
                    selectedFields = allFields
                        .Where(f => f.Fid.HasValue && f.IsReportable && !f.IsSystem && visibleFieldIds.Contains((long)f.Fid!.Value))
                        .ToList();
                }
            }
        }

        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && f.IsReportable && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
        }
        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && !f.IsHidden && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
        }

        return selectedFields;
    }
}
