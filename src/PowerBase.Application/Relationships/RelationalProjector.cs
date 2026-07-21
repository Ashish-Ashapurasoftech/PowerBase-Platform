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
        var referenceFields = fields.Where(f => f.TypeCode == "Reference" && f.Fid.HasValue).ToList();
        var lookupFields = fields.Where(f => f.TypeCode == "Lookup" && f.Fid.HasValue).ToList();
        var summaryFields = fields.Where(f => f.TypeCode == "Summary" && f.Fid.HasValue).ToList();
        if ((referenceFields.Count == 0 && lookupFields.Count == 0 && summaryFields.Count == 0) || rows.Count == 0)
            return rows.Select(_ => EmptyMap).ToList();

        var maps = new Dictionary<long, object?>[rows.Count];
        for (var i = 0; i < rows.Count; i++) maps[i] = new Dictionary<long, object?>();

        await ProjectReferencesAndLookupsAsync(referenceFields, lookupFields, rows, maps, ct);
        await ProjectSummariesAsync(table, summaryFields, rows, maps, ct);

        return maps;
    }

    private async Task ProjectReferencesAndLookupsAsync(
        IReadOnlyList<AppField> referenceFields,
        IReadOnlyList<AppField> lookupFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        var lookups = lookupFields
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseLookupSettings(f.Settings)))
            .Where(x => x.Settings is { SourceTableId: not null, ReferenceFid: not null, SourceFid: not null })
            .ToList();
            
        var refs = referenceFields
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseReferenceSettings(f.Settings)))
            .Where(x => x.Settings is { ParentTableId: not null })
            .ToList();
            
        var parentTableIds = lookups.Select(l => l.Settings!.SourceTableId!.Value)
            .Concat(refs.Select(r => r.Settings!.ParentTableId!.Value))
            .Distinct()
            .ToList();

        foreach (var parentTableId in parentTableIds)
        {
            var tableLookups = lookups.Where(l => l.Settings!.SourceTableId!.Value == parentTableId).ToList();
            var tableRefs = refs.Where(r => r.Settings!.ParentTableId!.Value == parentTableId).ToList();
            
            var parentTable = await _tableRepo.GetByIdAsync(parentTableId, ct);
            var parentFields = await _fieldRepo.ListByTableAsync(parentTableId, ct);
            var parentFieldsByFid = parentFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Fid!.Value);
            var keyField = await KeyFieldResolver.ResolveAsync(parentTable, _fieldRepo, ct);

            // The Fids of the Reference columns on THIS (child) table that point to the parent table.
            var refFids = tableLookups.Select(l => l.Settings!.ReferenceFid!.Value)
                .Concat(tableRefs.Select(r => r.Field.Fid!.Value))
                .Distinct().ToList();

            // The reference column ALWAYS stores the parent's Record ID# (bigint).
            // Fetch parent rows unconditionally by these IDs.
            var resolvedParentId = new Dictionary<(int RowIndex, int RefFid), long?>();
            var parentIds = new HashSet<long>();
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var refFid in refFids)
                {
                    long? pid = TryGetLong(rows[i], PhysicalNaming.ColumnName(refFid), out var v) ? v : null;
                    resolvedParentId[(i, refFid)] = pid;
                    if (pid is long id) parentIds.Add(id);
                }
            }
            var parentRows = await _recordRepo.GetRowsByIdsAsync(parentTable, parentFields, parentIds, ct);

            // Now map the values.
            for (var i = 0; i < rows.Count; i++)
            {
                // 1. Lookups
                foreach (var (field, settings) in tableLookups)
                {
                    object? value = null;
                    if (resolvedParentId.TryGetValue((i, settings!.ReferenceFid!.Value), out var pid) && pid is long parentId
                        && parentRows.TryGetValue(parentId, out var prow))
                    {
                        var srcCol = parentFieldsByFid.TryGetValue(settings.SourceFid!.Value, out var srcField)
                            ? (srcField.PhysicalColumnName ?? PhysicalNaming.ColumnName(settings.SourceFid.Value))
                            : PhysicalNaming.ColumnName(settings.SourceFid.Value);
                        if (prow.TryGetValue(srcCol, out var v)) value = v;
                    }
                    maps[i][field.Fid!.Value] = value;
                }
                
                // 2. References (Only if there is a Custom Key!)
                if (keyField != null)
                {
                    var customKeyCol = KeyFieldResolver.ColumnName(keyField);
                    foreach (var (field, settings) in tableRefs)
                    {
                        if (resolvedParentId.TryGetValue((i, field.Fid!.Value), out var pid) && pid is long parentId
                            && parentRows.TryGetValue(parentId, out var prow))
                        {
                            if (prow.TryGetValue(customKeyCol, out var customKeyValue))
                            {
                                // Store the custom key value string so RecordResult.FromRow can pick it up.
                                maps[i][field.Fid!.Value] = KeyFieldResolver.FormatForSubmit(customKeyValue);
                            }
                        }
                    }
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

        // The per-row value the child's reference column actually stores is ALWAYS the parent's row Id!
        var aggKeys = rowIds.Select(id => (object?)id).ToArray();
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
