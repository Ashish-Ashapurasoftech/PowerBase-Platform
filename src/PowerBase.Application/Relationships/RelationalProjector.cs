using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Compute-on-read projector for relationship fields. Batched (no N+1):
///   • Lookups  — one parent-row fetch per parent table, then map each child row's value.
///   • Summaries — one GROUP-BY aggregate per summary field, restricted to the page's parent Ids.
/// </summary>
public sealed class RelationalProjector : IRelationalProjector
{
    private static readonly IReadOnlyDictionary<long, object?> EmptyMap = new Dictionary<long, object?>();
    private static readonly JsonSerializerOptions FilterJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public RelationalProjector(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        var lookupFields = fields.Where(f => f.TypeCode == "Lookup" && f.Fid.HasValue).ToList();
        var summaryFields = fields.Where(f => f.TypeCode == "Summary" && f.Fid.HasValue).ToList();
        if ((lookupFields.Count == 0 && summaryFields.Count == 0) || rows.Count == 0)
            return rows.Select(_ => EmptyMap).ToList();

        var maps = new Dictionary<long, object?>[rows.Count];
        for (var i = 0; i < rows.Count; i++) maps[i] = new Dictionary<long, object?>();

        await ProjectLookupsAsync(lookupFields, rows, maps, ct);
        await ProjectSummariesAsync(summaryFields, rows, maps, ct);

        return maps;
    }

    private async Task ProjectLookupsAsync(
        IReadOnlyList<AppField> lookupFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        // Group by the parent (source) table so we fetch each parent table once.
        var byParent = lookupFields
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseLookupSettings(f.Settings)))
            .Where(x => x.Settings is { SourceTableId: not null, ReferenceFid: not null, SourceFid: not null })
            .GroupBy(x => x.Settings!.SourceTableId!.Value);

        foreach (var grp in byParent)
        {
            var parentTableId = grp.Key;
            var lookups = grp.ToList();

            var parentTable = await _tableRepo.GetByIdAsync(parentTableId, ct);
            var parentFields = await _fieldRepo.ListByTableAsync(parentTableId, ct);

            // Collect the parent Ids referenced by this page across all reference columns used here.
            var refFids = lookups.Select(l => l.Settings!.ReferenceFid!.Value).Distinct().ToList();
            var parentIds = new HashSet<long>();
            foreach (var row in rows)
                foreach (var refFid in refFids)
                    if (TryGetLong(row, PhysicalNaming.ColumnName(refFid), out var pid))
                        parentIds.Add(pid);

            var parentRows = await _recordRepo.GetRowsByIdsAsync(parentTable, parentFields, parentIds, ct);

            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var (field, settings) in lookups)
                {
                    object? value = null;
                    if (TryGetLong(rows[i], PhysicalNaming.ColumnName(settings!.ReferenceFid!.Value), out var pid)
                        && parentRows.TryGetValue(pid, out var prow)
                        && prow.TryGetValue(PhysicalNaming.ColumnName(settings.SourceFid!.Value), out var v))
                        value = v;
                    maps[i][field.Fid!.Value] = value;
                }
            }
        }
    }

    private async Task ProjectSummariesAsync(
        IReadOnlyList<AppField> summaryFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        if (summaryFields.Count == 0) return;

        // This table is the parent — gather its row Ids.
        var rowIds = new long[rows.Count];
        var parentIds = new HashSet<long>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (TryGetLong(rows[i], "Id", out var id)) { rowIds[i] = id; parentIds.Add(id); }
        }
        if (parentIds.Count == 0) return;

        foreach (var field in summaryFields)
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            if (s?.ChildTableId is not long childId || s.ReferenceFid is not int refFid || string.IsNullOrWhiteSpace(s.Function))
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            var childTable = await _tableRepo.GetByIdAsync(childId, ct);
            var filter = ParseFilter(s.FilterTree);
            var agg = await _recordRepo.AggregateByReferenceAsync(childTable, refFid, s.Function!, s.TargetFid, parentIds, filter, ct);

            var isCount = string.Equals(s.Function, SummaryFunctions.Count, StringComparison.OrdinalIgnoreCase);
            var isExists = string.Equals(s.Function, SummaryFunctions.Exists, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                // No matching children: Count → 0, Exists → false, others → null.
                if (agg.TryGetValue(rowIds[i], out var v)) maps[i][field.Fid!.Value] = v;
                else maps[i][field.Fid!.Value] = isCount ? 0 : isExists ? false : null;
            }
        }
    }

    private static bool TryGetLong(IReadOnlyDictionary<string, object?> row, string key, out long value)
    {
        value = 0;
        if (!row.TryGetValue(key, out var raw) || raw is null) return false;
        try { value = Convert.ToInt64(raw); return true; }
        catch { return long.TryParse(raw.ToString(), out value); }
    }

    private static FilterGroup? ParseFilter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FilterGroup>(json, FilterJsonOpts); }
        catch (JsonException) { return null; }
    }
}
