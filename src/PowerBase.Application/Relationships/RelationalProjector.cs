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
        await ProjectSummariesAsync(table, summaryFields, rows, maps, ct);

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
            var parentFieldsByFid = parentFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Fid!.Value);
            var keyField = await KeyFieldResolver.ResolveAsync(parentTable, _fieldRepo, ct);
            var refFids = lookups.Select(l => l.Settings!.ReferenceFid!.Value).Distinct().ToList();

            IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> parentRows;
            // Per (rowIndex, refFid): the resolved parent row Id, or null when unresolved/blank.
            var resolvedParentId = new Dictionary<(int RowIndex, int RefFid), long?>();

            if (keyField is null)
            {
                // Default key (Record ID#): the reference column already stores the parent row Id —
                // exact same behavior as before Set Key existed.
                var parentIds = new HashSet<long>();
                for (var i = 0; i < rows.Count; i++)
                    foreach (var refFid in refFids)
                    {
                        long? pid = TryGetLong(rows[i], PhysicalNaming.ColumnName(refFid), out var v) ? v : null;
                        resolvedParentId[(i, refFid)] = pid;
                        if (pid is long id) parentIds.Add(id);
                    }
                parentRows = await _recordRepo.GetRowsByIdsAsync(parentTable, parentFields, parentIds, ct);
            }
            else
            {
                // Custom key: the reference column stores the key field's raw value — resolve it back
                // to a row Id first, then reuse the existing Id-based row fetch unchanged.
                var col = KeyFieldResolver.ColumnName(keyField);
                var rawByRowRef = new Dictionary<(int, int), object>();
                for (var i = 0; i < rows.Count; i++)
                    foreach (var refFid in refFids)
                        if (rows[i].TryGetValue(PhysicalNaming.ColumnName(refFid), out var raw) && raw is not null)
                            rawByRowRef[(i, refFid)] = raw;

                var idsByKey = await _recordRepo.GetIdsByColumnValuesAsync(parentTable, col, rawByRowRef.Values.Distinct().ToList(), ct);
                foreach (var ((i, refFid), raw) in rawByRowRef)
                    resolvedParentId[(i, refFid)] = idsByKey.TryGetValue(raw, out var id) ? id : (long?)null;

                parentRows = await _recordRepo.GetRowsByIdsAsync(parentTable, parentFields, idsByKey.Values.Distinct().ToList(), ct);
            }

            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var (field, settings) in lookups)
                {
                    object? value = null;
                    if (resolvedParentId.TryGetValue((i, settings!.ReferenceFid!.Value), out var pid) && pid is long parentId
                        && parentRows.TryGetValue(parentId, out var prow))
                    {
                        // Resolve via the source field's actual physical column — not always "f_{fid}"
                        // (e.g. Record ID# is backed by the row's "Id" column, not "f_3").
                        var srcCol = parentFieldsByFid.TryGetValue(settings.SourceFid!.Value, out var srcField)
                            ? (srcField.PhysicalColumnName ?? PhysicalNaming.ColumnName(settings.SourceFid.Value))
                            : PhysicalNaming.ColumnName(settings.SourceFid.Value);
                        if (prow.TryGetValue(srcCol, out var v)) value = v;
                    }
                    maps[i][field.Fid!.Value] = value;
                }
            }
        }
    }

    private async Task ProjectSummariesAsync(
        AppTable table,
        IReadOnlyList<AppField> summaryFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        if (summaryFields.Count == 0) return;

        // This table is the parent — gather its row Ids.
        var rowIds = new long[rows.Count];
        var idSet = new HashSet<long>();
        for (var i = 0; i < rows.Count; i++)
            if (TryGetLong(rows[i], "Id", out var id)) { rowIds[i] = id; idSet.Add(id); }
        if (idSet.Count == 0) return;

        // The per-row value the child's reference column actually stores: the row Id for the default
        // key, or this table's key-field value for a Set-Key table.
        var keyField = await KeyFieldResolver.ResolveAsync(table, _fieldRepo, ct);
        object?[] aggKeys;
        if (keyField is null)
        {
            aggKeys = rowIds.Select(id => (object?)id).ToArray();
        }
        else
        {
            var col = KeyFieldResolver.ColumnName(keyField);
            var keyValues = await _recordRepo.GetColumnValuesByIdsAsync(table, col, idSet, ct);
            aggKeys = rowIds.Select(id => keyValues.TryGetValue(id, out var v) ? v : null).ToArray();
        }
        var parentKeyValues = aggKeys.Where(k => k is not null).Select(k => k!).Distinct().ToList();
        if (parentKeyValues.Count == 0) return;

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
            var agg = await _recordRepo.AggregateByReferenceAsync(childTable, refFid, s.Function!, s.TargetFid, parentKeyValues, filter, ct);

            var isCount = string.Equals(s.Function, SummaryFunctions.Count, StringComparison.OrdinalIgnoreCase);
            var isExists = string.Equals(s.Function, SummaryFunctions.Exists, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                // No matching children: Count → 0, Exists → false, others → null.
                if (aggKeys[i] is object key && agg.TryGetValue(key, out var v)) maps[i][field.Fid!.Value] = v;
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
