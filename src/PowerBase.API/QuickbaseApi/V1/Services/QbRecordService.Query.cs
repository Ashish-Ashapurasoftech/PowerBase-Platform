using PowerBase.Application.Common.Interfaces;
using System.Globalization;
using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// Quickbase "Query for data" (POST /v1/records/query): <c>{from, select, where, sortBy, groupBy, options{skip, top}}</c> →
/// <c>{data, fields, metadata}</c>. Reads go through the role's view scope / view filter and hidden fields like a report run;
/// a filter or sort on an encrypted or calculated field is judged on the decrypted / computed values in memory.
/// </summary>
public sealed partial class QbRecordService
{
    /// <summary>Rows read per SQL page on the fast path.</summary>
    public const int QUERY_PAGE_SIZE = 500;

    public Task<QbRunReportResponse> QueryAsync(QbQueryRequest request, CancellationToken ct) => QueryAsync(request, ct, null);

    /// <param name="onRecord">Called with each returned record as our record handler read it (XML API_DoQuery needs its
    /// update_id and raw stored values).</param>
    public async Task<QbRunReportResponse> QueryAsync(QbQueryRequest request, CancellationToken ct,
        Action<long, Application.Records.RecordResult>? onRecord)
    {
        if (string.IsNullOrWhiteSpace(request.From)) throw QbApiException.BadRequest("Required key 'from' not found");
        var tableId = await TableOrInvalidRequestAsync(request.From, "from", ct);
        await _access.RequireMembershipByTablePublicIdAsync(tableId, ct);
        var table = await _tables.GetByPublicIdAsync(tableId, ct);
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView) throw QbApiException.AccessDenied("You do not have permission to view records in this table.");
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var visible = (access.Unrestricted ? fields : access.VisibleFields).Where(f => f.Fid.HasValue && !f.IsDeleted)
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        visible.Add(QbIds.RECORD_ID_FID);

        var skip = request.Options?.Skip ?? 0;
        if (skip < 0) throw QbApiException.BadRequest("'options.skip' must be >= 0");
        if (request.Options?.Top is <= 0) throw QbApiException.BadRequest("'options.top' must be greater than zero");
        var take = Math.Min(request.Options?.Top ?? QbReportService.DEFAULT_TOP, QbReportService.MAX_TOP);

        // select: the fields to return. Quickbase returns the table's default columns when it is left out; here that is
        // every field the caller may see.
        var select = (request.Select ?? new()).Select(e => (long)QbIds.ParseFid(e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString(), "select"))
            .Distinct().ToList();
        // Live realm: a select id the table does not have is skipped (200), not an error.
        select = select.Where(fid => byFid.ContainsKey(fid) || fid == QbIds.RECORD_ID_FID).ToList();
        if (select.Count == 0 && (request.Select is null || request.Select.Count == 0))
            // Live realm: no select = the table's default columns (fields shown by default; the built-in fields 1-5 are not).
            select = fields.Where(f => f.Fid.HasValue && !f.IsDeleted && visible.Contains(f.Fid.Value) && !QbFieldShape.IsSystem(f)
                    && QbFieldShape.AppearsByDefault(f)).Select(f => (long)f.Fid!.Value)
                .Distinct().OrderBy(f => f).ToList();
        var returned = select.Where(visible.Contains).ToList(); // a hidden field is left out, as in Quickbase

        var sort = SortOf(request, byFid);
        foreach (var s in sort)
            if (!byFid.ContainsKey(s.FieldId) && s.FieldId != QbIds.RECORD_ID_FID) throw QbApiException.BadRequest($"Unknown field with Id \"{s.FieldId}\".");
        // Live realm: rows that tie on the requested sort keep the table's default sort (List All), then Record ID# ascending.
        if (sort.Count > 0)
        {
            foreach (var d in await DefaultSortAsync(table.PublicId, ct))
                if (byFid.ContainsKey(d.FieldId) && sort.All(s => s.FieldId != d.FieldId)) sort.Add(d);
            if (sort.All(s => s.FieldId != QbIds.RECORD_ID_FID)) sort.Add(new SortSpec { FieldId = QbIds.RECORD_ID_FID });
        }

        var knownFids = fields.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToHashSet();
        var appRow = await _apps.GetByIdAsync(table.AppId, ct);
        string? QbType(long fid) => byFid.TryGetValue(fid, out var tf) ? QbFieldPropertiesBuilder.TypeOf(tf).FieldType : null;
        FilterGroup? where;
        try
        {
            where = string.IsNullOrWhiteSpace(request.Where) ? null
                : QbWhereParser.Parse(request.Where, knownFids, QbType, QbTimeZone.OfApp(appRow.Formatting));
        }
        catch (QbApiException e) when (e.StatusCode == 400 && e.Description.StartsWith("Invalid query", StringComparison.Ordinal))
        {
            throw new QbApiException(400, "Invalid request", "Criteria is invalid.");   // live realm wording for a query it cannot read
        }
        var filter = new FilterGroup { Logic = "AND" };
        if (where is not null) filter.Nodes.Add(new FilterNode { Group = where });
        if (access.ViewFilter is { } vf) filter.Nodes.Add(new FilterNode { Group = vf });
        var filterTree = filter.Nodes.Count > 0 ? filter : null;

        if (!_appEncrypted.TryGetValue(table.AppId, out var appEncrypted))
            _appEncrypted[table.AppId] = appEncrypted = (await _apps.GetByIdAsync(table.AppId, ct)).IsEncrypted;
        bool Computed(long fid) => byFid.TryGetValue(fid, out var f) && (PhysicalNaming.IsComputedTypeCode(f.TypeCode));
        bool Encrypted(long fid) => byFid.TryGetValue(fid, out var f) && EncryptedRowFilter.IsEncrypted(f, appEncrypted);
        var filterFids = FilterFids(filterTree).ToList();
        // A phone condition compares digits only (Quickbase), which SQL cannot do on the stored text: judged in memory.
        var phoneFids = (where is null ? Enumerable.Empty<long>() : FilterFids(where)).Where(f => QbType(f) == "phone").Distinct().ToList();
        var inMemory = EncryptedRowFilter.TouchesEncrypted(filterTree, fields, appEncrypted)
                       || phoneFids.Count > 0
                       || filterFids.Any(Computed) || sort.Any(s => Computed(s.FieldId) || Encrypted(s.FieldId));
        // Quickbase orders a choice field by its choice list and a blank number just above 0. SQL cannot, so such a sort is done
        // in memory — for tables up to QB_ORDER_MAX_ROWS rows (bigger ones keep the plain SQL order).
        if (!inMemory && sort.Count > 0
            && await _records.CountAsync(table, fields, filterTree, access.RestrictToCreatedBy, ct) <= QB_ORDER_MAX_ROWS)
            inMemory = true;

        int total;
        List<(long Rid, Guid PublicId)> page;
        if (!inMemory)
        {
            total = await _records.CountAsync(table, fields, filterTree, access.RestrictToCreatedBy, ct);
            page = new();
            var sqlPage = skip / QUERY_PAGE_SIZE + 1;
            var offset = skip % QUERY_PAGE_SIZE;
            while (page.Count < take && (sqlPage - 1) * QUERY_PAGE_SIZE < total)
            {
                var rows = await _records.ListAsync(table, fields, sqlPage, QUERY_PAGE_SIZE, filterTree, sort.Count > 0 ? sort : null,
                    access.RestrictToCreatedBy, ct);
                page.AddRange(rows.Skip(offset).Select(RowKey).Take(take - page.Count));
                offset = 0;
                if (rows.Count < QUERY_PAGE_SIZE) break;
                sqlPage++;
            }
        }
        else
        {
            // Every row is read (in pages, decrypted) and judged in memory, as deleteRecords does for encrypted conditions; only
            // the matching rows' ids and sort values are kept.
            var readFids = filterFids.Concat(sort.Select(s => s.FieldId)).Where(Computed).Distinct().ToList();
            var kept = new List<(IReadOnlyDictionary<string, object?> Row, Dictionary<long, object?> Values)>();
            const int memoryPage = 5000;
            for (var memPage = 1; ; memPage++)
            {
                var rows = await _records.ListAsync(table, fields, memPage, memoryPage, filterTree: null,
                    restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
                var computed = new List<IReadOnlyDictionary<long, object?>>(rows.Count);
                foreach (var row in rows)
                {
                    var values = new Dictionary<long, object?>();
                    if (readFids.Count > 0)
                    {
                        var record = await _get.HandleAsync(new GetRecordQuery(tableId, RowKey(row).PublicId), ct);
                        foreach (var fid in readFids) values[fid] = record.Fields.GetValueOrDefault(fid.ToString());
                    }
                    if (phoneFids.Count > 0)
                    {
                        var stored = EncryptedRowFilter.ValuesByFid(row, fields);
                        foreach (var fid in phoneFids)
                            if (!Computed(fid)) values[fid] = stored.GetValueOrDefault(fid) is { } pv && QbWhereParser.PhoneDigits(pv.ToString()) is { Length: > 0 } d ? d : null;
                            else if (values.GetValueOrDefault(fid) is { } cv) values[fid] = QbWhereParser.PhoneDigits(cv.ToString());
                    }
                    computed.Add(values);
                }
                var matches = filterTree is null ? null : EncryptedRowFilter.MatchingIds(rows, fields, filterTree, computed).ToHashSet();
                for (var i = 0; i < rows.Count; i++)
                {
                    var key = RowKey(rows[i]);
                    if (matches is not null && !matches.Contains(key.Rid)) continue;
                    var slim = new Dictionary<string, object?> { ["Id"] = key.Rid, ["PublicId"] = key.PublicId };
                    var all = Merge(EncryptedRowFilter.ValuesByFid(rows[i], fields), computed[i], rows[i]);
                    kept.Add((slim, sort.Count == 0 ? new Dictionary<long, object?>() : sort.ToDictionary(s => s.FieldId,
                        s => byFid.TryGetValue(s.FieldId, out var sf) ? QbOrderValue(sf, all.GetValueOrDefault(s.FieldId)) : all.GetValueOrDefault(s.FieldId))));
                }
                if (rows.Count < memoryPage) break;
            }
            IEnumerable<(IReadOnlyDictionary<string, object?> Row, Dictionary<long, object?> Values)> ordered = kept;
            if (sort.Count > 0)
            {
                IOrderedEnumerable<(IReadOnlyDictionary<string, object?> Row, Dictionary<long, object?> Values)>? o = null;
                foreach (var s in sort)
                {
                    object? Key((IReadOnlyDictionary<string, object?> Row, Dictionary<long, object?> Values) x) => x.Values.GetValueOrDefault(s.FieldId);
                    o = o is null ? (s.Desc ? kept.OrderByDescending(Key, SortComparer.Instance) : kept.OrderBy(Key, SortComparer.Instance))
                                  : (s.Desc ? o.ThenByDescending(Key, SortComparer.Instance) : o.ThenBy(Key, SortComparer.Instance));
                }
                ordered = o!.ThenBy(x => Convert.ToInt64(x.Row["Id"], CultureInfo.InvariantCulture));
            }
            total = kept.Count;
            page = ordered.Skip(skip).Take(take).Select(x => RowKey(x.Row)).ToList();
        }

        var refTypes = await _referenceTypes.ForAsync(fields, ct);
        var qbFields = returned.Select(fid => fid == QbIds.RECORD_ID_FID && !byFid.ContainsKey(fid)
            ? new QbReportField(fid, "Record ID#", "recordid", null)
            : new QbReportField(fid, byFid[fid].Label ?? byFid[fid].Name, QbFieldPropertiesBuilder.TypeOf(byFid[fid], refTypes).FieldType, null)).ToList();
        var userFields = returned.Where(f => byFid.TryGetValue(f, out var x) && QbUserValueResolver.IsStoredUserField(x)).Select(f => byFid[f]).ToList();
        var users = await _userValues.ResolveAsync(table, userFields, page.Select(p => p.PublicId).ToList(), ct);
        var appPublicId = await _apps.GetPublicIdByIdAsync(table.AppId, ct);
        var fileBase = $"/apps/{appPublicId}/files/download";
        var fids = returned.Select(f => (int)f).ToList();
        var data = new List<IDictionary<string, QbCell>>(page.Count);
        foreach (var (rid, publicId) in page)
            data.Add(await ReadFieldsAsync(tableId, rid, publicId, fids, byFid, users.GetValueOrDefault(publicId), fileBase, refTypes, ct, onRecord));
        await _userValues.EnrichNamedUsersAsync(appPublicId, data, NamedUserKeys(returned, byFid, refTypes), ct);

        return new QbRunReportResponse
        {
            Data = data,
            Fields = qbFields,
            Metadata = new QbRunMetadata
            {
                TotalRecords = total,
                NumRecords = data.Count,
                NumFields = qbFields.Count,
                Skip = skip,
                Top = request.Options?.Top,
            },
        };
    }

    private static (long Rid, Guid PublicId) RowKey(IReadOnlyDictionary<string, object?> row) =>
        (Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture), row["PublicId"] is Guid g ? g : Guid.Parse(row["PublicId"]!.ToString()!));

    private static Dictionary<long, object?> Merge(IReadOnlyDictionary<long, object?> stored, IReadOnlyDictionary<long, object?> computed,
        IReadOnlyDictionary<string, object?> row)
    {
        var all = new Dictionary<long, object?>(stored);
        foreach (var (k, v) in computed) all[k] = v;
        all[QbIds.RECORD_ID_FID] = row.GetValueOrDefault("Id");
        return all;
    }

    /// <summary>sortBy: <c>[{fieldId, order: ASC|DESC|equal-values}]</c>, or <c>false</c> for no sort. groupBy fields sort first,
    /// ascending, as Quickbase groups them.</summary>
    public static List<SortSpec> SortOf(QbQueryRequest request, IReadOnlyDictionary<long, AppField> byFid)
    {
        var sort = new List<SortSpec>();
        foreach (var g in request.GroupBy ?? new())
            if (g.FieldId is { } gf && sort.All(s => s.FieldId != gf)) sort.Add(new SortSpec { FieldId = gf });
        if (request.SortBy is { ValueKind: JsonValueKind.Array } list)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("fieldId", out var idEl))
                    throw QbApiException.BadRequest("'sortBy' items must have a 'fieldId'");
                var fid = QbIds.ParseFid(idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString(), "sortBy.fieldId");
                var order = item.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : "ASC";
                if (sort.All(s => s.FieldId != fid))
                    sort.Add(new SortSpec { FieldId = fid, Desc = string.Equals(order, "DESC", StringComparison.OrdinalIgnoreCase) });
            }
        else if (request.SortBy is { } other && other.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined))
            throw QbApiException.BadRequest("'sortBy' must be an array or false");
        return sort;
    }

    private static IEnumerable<long> FilterFids(FilterGroup? tree)
    {
        if (tree is null) yield break;
        foreach (var node in tree.Nodes)
        {
            if (node.Condition is { } c)
            {
                yield return c.FieldId;
                if (c.ValueFieldId is long other) yield return other;
            }
            foreach (var fid in FilterFids(node.Group)) yield return fid;
        }
    }

    /// <summary>Blank first (ascending), then numbers numerically, dates by time, text case-insensitively.</summary>
    /// <summary>Rows considered by one in-memory Quickbase-order sort at most.</summary>
    public const int QB_ORDER_MAX_ROWS = 20000;

    /// <summary>1 = choice field (ordered by its choice list), 2 = number whose blank counts just above 0; 0 = plain order.</summary>
    public static int QbOrderKind(AppField f)
    {
        var t = QbFieldPropertiesBuilder.TypeOf(f);
        if (t.Mode != "") return 0;
        if (t.FieldType == "text-multiple-choice" && QbFieldPropertiesBuilder.BuildProperties(f).GetValueOrDefault("choices") is IEnumerable<string>) return 1;
        if (t.FieldType is "numeric" or "currency" or "percent" && QbFieldShape.Properties(f, t).GetValueOrDefault("blankIsZero") is true) return 2;
        if (t.FieldType == "checkbox") return 3;
        return 0;
    }

    /// <summary>The sort value Quickbase uses (live realm): a choice's position in its list (blank first), a blank number = 0 + ε.</summary>
    public static object? QbOrderValue(AppField f, object? value)
    {
        switch (QbOrderKind(f))
        {
            case 1:
            {
                var text = value?.ToString();
                if (string.IsNullOrEmpty(text)) return -1m;
                var choices = ((IEnumerable<string>)QbFieldPropertiesBuilder.BuildProperties(f)["choices"]!).ToList();
                var i = choices.FindIndex(c => string.Equals(c, text, StringComparison.Ordinal));
                return i >= 0 ? (decimal)i : choices.Count + 0.5m;   // a value outside the list: after the listed ones
            }
            case 2:
                return value is null || value is string { Length: 0 } ? 0.0000000001m : value;
            case 3:
                return value switch                                            // a blank box sorts as unchecked
                {
                    bool b => b ? 1m : 0m,
                    string sv => sv.Trim() is "1" or "true" or "True" ? 1m : 0m,
                    IConvertible c => Convert.ToDecimal(c, CultureInfo.InvariantCulture) != 0 ? 1m : 0m,
                    _ => 0m,
                };
            default:
                return value;
        }
    }

    /// <summary>The table's default (List All) report sort, used to break ties as Quickbase does.</summary>
    private async Task<IReadOnlyList<SortSpec>> DefaultSortAsync(Guid tablePublicId, CancellationToken ct)
    {
        if (_services?.GetService(typeof(IReportRepository)) is not IReportRepository reports
            || _services.GetService(typeof(QbReportService)) is not QbReportService reportService) return Array.Empty<SortSpec>();
        try
        {
            if (await reports.GetDefaultByTableAsync(tablePublicId, ct) is not { } def) return Array.Empty<SortSpec>();
            var r = await reportService.GetReportAsync(def.PublicId, tablePublicId, ct);
            return (r.Query.SortBy ?? Array.Empty<QbSortBy>()).Select(s => new SortSpec { FieldId = s.FieldId, Desc = s.Order == "DESC" }).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Array.Empty<SortSpec>(); }
    }

    private sealed class SortComparer : IComparer<object?>
    {
        public static readonly SortComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            var a = Norm(x);
            var b = Norm(y);
            if (a is null) return b is null ? 0 : -1;
            if (b is null) return 1;
            if (a is decimal da && b is decimal db) return da.CompareTo(db);
            if (a is DateTime ta && b is DateTime tb) return ta.CompareTo(tb);
            // Live realm: text sorts by letters, accents with their base letter (Ü with U), case ignored.
            return string.Compare(a.ToString(), b.ToString(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        }

        private static object? Norm(object? v) => v switch
        {
            null => null,
            string s when s.Length == 0 => null,
            bool bo => bo ? 1m : 0m,
            DateTime d => d,
            DateTimeOffset o => o.UtcDateTime,
            IConvertible c when v is not string && decimal.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n,
            string s when s.Length >= 10 && char.IsDigit(s[0]) && s[4] == '-' && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p) => p,
            JsonElement j => j.ToString(),
            _ => v,
        };
    }
}
