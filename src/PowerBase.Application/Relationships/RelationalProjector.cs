using System.Text.Json;
using PowerBase.Application.Common.Formatting;
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
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppRepository _appRepo;

    /// <summary>Each app's display formatting, loaded once per projector (scoped per request) the
    /// first time a Combined Text summary needs it — keyed by AppId, since one scope (e.g. a
    /// pipeline copy) can project tables from more than one app.</summary>
    private readonly Dictionary<long, Domain.ValueObjects.AppFormattingSettings> _appFormatting = new();
    private readonly Dictionary<long, App> _apps = new();

    public RelationalProjector(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        IRelationshipRepository relRepo, IAppRepository appRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _relRepo = relRepo;
        _appRepo = appRepo;
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

        await ProjectReferencesAndLookupsAsync(table, referenceFields, lookupFields, rows, maps, ct);
        await ProjectSummariesAsync(table, summaryFields, rows, maps, ct);

        return maps;
    }

    private async Task ProjectReferencesAndLookupsAsync(
        AppTable childTable,
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

        // The child table's relationships — used to pick each reference field's display key
        // (per-relationship DisplayKeyFieldId override → parent table key → Record ID#).
        Dictionary<long, Relationship> relsById = new();
        Dictionary<int, Relationship> relsByRefFid = new();
        if (refs.Count > 0)
        {
            var childRels = await _relRepo.ListByChildTableAsync(childTable.Id, ct);
            relsById = childRels.ToDictionary(r => r.Id);
            relsByRefFid = childRels.GroupBy(r => r.ReferenceFid).ToDictionary(g => g.Key, g => g.First());
        }

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

            // Resolve each reference field's display key once (null ⇒ Record ID#, stored row Id shows as-is).
            var displayKeyByRefFid = new Dictionary<int, AppField?>();
            foreach (var (field, settings) in tableRefs)
            {
                var rel = settings!.RelationshipId is long relId && relsById.TryGetValue(relId, out var r)
                    ? r
                    : relsByRefFid.GetValueOrDefault(field.Fid!.Value);
                displayKeyByRefFid[field.Fid!.Value] = KeyFieldResolver.ResolveDisplayKey(rel, parentTable, parentFields);
            }

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
                        if (prow.TryGetValue(srcCol, out var v))
                            value = string.IsNullOrWhiteSpace(settings.SourceSubField) ? v : ExtractJsonSubField(v, settings.SourceSubField);
                    }
                    maps[i][field.Fid!.Value] = value;
                }
                
                // 2. References — surface the display-key value (Id → readable) for each reference
                //    field. When the display key is Record ID# (null) the stored row Id already shows,
                //    so there is nothing to project.
                foreach (var (field, _) in tableRefs)
                {
                    if (displayKeyByRefFid[field.Fid!.Value] is not AppField displayKey) continue;
                    var displayKeyCol = KeyFieldResolver.ColumnName(displayKey);
                    if (resolvedParentId.TryGetValue((i, field.Fid!.Value), out var pid) && pid is long parentId
                        && parentRows.TryGetValue(parentId, out var prow)
                        && prow.TryGetValue(displayKeyCol, out var displayKeyValue))
                    {
                        // Store the display-key value string so RecordResult.FromRow can pick it up.
                        maps[i][field.Fid!.Value] = KeyFieldResolver.FormatForSubmit(displayKeyValue);
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

        var app = await GetAppAsync(table.AppId, ct);
        var childFieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();

        foreach (var field in summaryFields)
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            // Canonical casing: rows imported before names were normalized may say "sum".
            var function = SummaryFunctions.Normalize(s?.Function);
            if (s?.ChildTableId is not long childId || s.ReferenceFid is not int refFid || function is null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            if (!childFieldsByTable.TryGetValue(childId, out var childFields))
            {
                childFields = await _fieldRepo.ListByTableAsync(childId, ct);
                childFieldsByTable[childId] = childFields;
            }
            var filter = ParseFilter(s.FilterTree);

            // A summary reading an encrypted column is shown blank and never computed: SQL can't
            // aggregate ciphertext, and Combined Text would otherwise put it on screen.
            var sortFid = function == SummaryFunctions.CombinedText ? s.SortFid : null;
            if (SummaryEncryptionGuard.FindProblem(app, childFields, refFid, s.TargetFid, sortFid, filter) is not null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            // A summary saved before today's rules (e.g. Sum over a formula field, which has no column
            // and would fail the whole read, or Combined Text over a User field, which would show raw
            // ids) is shown blank rather than computed wrong — same rule as creation.
            if (s.TargetFid is int tFid && childFields.FirstOrDefault(f => f.Fid == tFid) is { } targetField
                && SummaryTargetValidator.FindProblem(function, targetField, s.TargetSubField) is not null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            var childTable = await _tableRepo.GetByIdAsync(childId, ct);
            var agg = function == SummaryFunctions.CombinedText && s.TargetFid is int targetFid
                ? await CombineTextAsync(app, childTable, childFields, refFid, targetFid, s, parentKeyValues, filter, ct)
                : await _recordRepo.AggregateByReferenceAsync(childTable, refFid, function, s.TargetFid, parentKeyValues, filter, s.TargetSubField, ct);

            // Match results to parents by value, not by boxed type: a reference that was converted
            // from a Number field is a DECIMAL column, so its keys come back as 42.0000m while the
            // parent row Ids here are 42L — and a decimal never equals a long as an object key.
            var aggByKey = new Dictionary<string, object?>();
            foreach (var (key, value) in agg)
                if (NormalizeParentKey(key) is { } k) aggByKey[k] = value;

            var isCount = function is SummaryFunctions.Count or SummaryFunctions.DistinctCount;
            var isExists = function == SummaryFunctions.Exists;
            for (var i = 0; i < rows.Count; i++)
            {
                // No matching children: Count/DistinctCount → 0, Exists → false, others → null.
                if (NormalizeParentKey(aggKeys[i]) is { } key && aggByKey.TryGetValue(key, out var v)) maps[i][field.Fid!.Value] = v;
                else maps[i][field.Fid!.Value] = isCount ? 0 : isExists ? false : null;
            }
        }
    }

    /// <summary>A parent key's canonical text — numbers by value (42L, 42.0000m → "42"), anything
    /// else as its trimmed text — so keys of different CLR types still match.</summary>
    internal static string? NormalizeParentKey(object? key) => key switch
    {
        null => null,
        decimal or double or float or long or int or short or byte =>
            Convert.ToDecimal(key, System.Globalization.CultureInfo.InvariantCulture)
                .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)?.Trim(),
    };

    /// <summary>The app (encryption flag + display formatting), loaded once per app per projector
    /// (scoped per request); one scope — e.g. a pipeline copy — can project more than one app.</summary>
    private async Task<App> GetAppAsync(long appId, CancellationToken ct)
    {
        if (!_apps.TryGetValue(appId, out var app))
        {
            app = await _appRepo.GetByIdAsync(appId, ct);
            _apps[appId] = app;
        }
        return app;
    }

    /// <summary>
    /// Combined Text: each child value is rendered with its field's display format (the app's
    /// Formatting + the field's own Behavior Settings — "$1,200.00", "09-23-2026", "2 hrs"), blanks
    /// dropped, then joined with the delimiter. Distinct compares the displayed text, so values that
    /// differ only in storage (100 vs 100.0000) still collapse. Returns parentKey → text; parents
    /// with nothing to show are absent (→ null).
    /// </summary>
    private async Task<IReadOnlyDictionary<object, object?>> CombineTextAsync(
        App app, AppTable childTable, IReadOnlyList<AppField> childFields, int refFid, int targetFid, SummarySettings s,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filter, CancellationToken ct)
    {
        var options = CombinedTextOptions.From(s);
        var rows = await _recordRepo.ListValuesByReferenceAsync(childTable, refFid, targetFid, s.TargetSubField,
            parentKeyValues, filter, options.SortFid, options.SortDescending, ct);
        if (rows.Count == 0) return new Dictionary<object, object?>();

        // An Address sub-key is plain text; otherwise format as the target field currently is.
        var target = childFields.FirstOrDefault(f => f.Fid == targetFid);
        var typeCode = string.IsNullOrWhiteSpace(s.TargetSubField) ? target?.TypeCode ?? s.TargetTypeCode ?? "Text" : "Text";
        if (!_appFormatting.TryGetValue(app.Id, out var appFormatting))
        {
            appFormatting = DisplayValueFormatter.ParseAppFormatting(app.Formatting);
            _appFormatting[app.Id] = appFormatting;
        }

        var result = new Dictionary<object, object?>();
        foreach (var group in rows.GroupBy(r => r.ParentKey))
        {
            var texts = group
                .Select(r => DisplayValueFormatter.Format(r.Value, typeCode, target?.Settings, appFormatting))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!);
            if (options.DistinctValues) texts = texts.Distinct(StringComparer.Ordinal);
            var joined = string.Join(options.Delimiter, texts);
            if (joined.Length > 0) result[group.Key] = joined;
        }
        return result;
    }

    /// <summary>Pulls one JSON property out of a composite Address field's raw stored value
    /// (e.g. just the city). Returns null on any parse failure rather than throwing — malformed
    /// or legacy data shouldn't take down the whole record read.</summary>
    private static object? ExtractJsonSubField(object? rawValue, string subField)
    {
        if (rawValue is not string json || string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(subField, out var prop) && prop.ValueKind != JsonValueKind.Null
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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
