using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pages;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>The fields a dashboard names itself. A Report widget's own columns and filters are already counted
/// through that report, so only what the dashboard adds counts here: the field each filter slot is bound to (on the
/// widget's report table) and the fields a Search widget searches (on its target table). Both are stored as the
/// per-table field number (Fid), like a report's.
///
/// A dashboard can touch several tables, so the rows come back grouped by the table each field lives on —
/// <see cref="FieldReferenceCollector"/> files every row of one collector under a single table.</summary>
public static class DashboardReferenceExtractor
{
    /// <summary>Resolves what a dashboard needs to place a field: a report's table, a table by public id, a table's fields.</summary>
    public interface IResolver
    {
        long? TableOfReport(Guid reportPublicId);
        Task<long?> TableByPublicIdAsync(Guid tablePublicId, CancellationToken ct);
        Task<TableFieldIndex?> TableAsync(long tableId, CancellationToken ct);
    }

    /// <summary>The reports a dashboard's widgets run — their tables are needed to place the filter bindings.</summary>
    public static IReadOnlyCollection<Guid> ReportIds(string definition)
    {
        try
        {
            return DashboardDefinition.Parse(definition).Tabs
                .SelectMany(t => t.Widgets)
                .Where(w => w.FilterBindings.Count > 0 && w.ReportPublicId != Guid.Empty)
                .Select(w => w.ReportPublicId)
                .Distinct()
                .ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static async Task<IReadOnlyCollection<FieldReferenceRow>> ExtractAsync(DashboardSource page, IResolver resolver, CancellationToken ct)
    {
        DashboardDefinition definition;
        try { definition = DashboardDefinition.Parse(page.Definition); }
        catch (System.Text.Json.JsonException) { return []; }

        var collectors = new Dictionary<long, FieldReferenceCollector>();
        FieldReferenceCollector CollectorFor(long tableId) =>
            collectors.TryGetValue(tableId, out var existing)
                ? existing
                : collectors[tableId] = new FieldReferenceCollector(FieldReferenceSourceTypes.Dashboard, page.Id, tableId);

        foreach (var widget in definition.Tabs.SelectMany(t => t.Widgets))
        {
            // Filter slots bound to a field of the widget's report.
            if (widget.FilterBindings.Count > 0 && resolver.TableOfReport(widget.ReportPublicId) is { } reportTable
                && await resolver.TableAsync(reportTable, ct) is { } reportIndex)
            {
                foreach (var binding in widget.FilterBindings.Values)
                    CollectorFor(reportTable).Add(reportIndex.IdOfFid(binding.FieldId), FieldReferenceUsages.DashboardFilter);
            }

            // A Search widget limited to chosen fields of one table.
            if (widget.WidgetType == DashboardWidgetTypes.Search && widget.SearchScope == "SelectedFields"
                && widget.SearchTargetTableId is { } searchTablePublicId && widget.SearchFieldIds.Count > 0
                && await resolver.TableByPublicIdAsync(searchTablePublicId, ct) is { } searchTable
                && await resolver.TableAsync(searchTable, ct) is { } searchIndex)
            {
                foreach (var fid in widget.SearchFieldIds)
                    CollectorFor(searchTable).Add(searchIndex.IdOfFid(fid), FieldReferenceUsages.DashboardSearch);
            }
        }

        return collectors.Values.SelectMany(c => c.Rows).ToList();
    }
}
