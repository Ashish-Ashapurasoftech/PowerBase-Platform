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

    public async Task<QbRunReportResponse> QueryAsync(QbQueryRequest request, CancellationToken ct)
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
        foreach (var fid in select)
            if (!byFid.ContainsKey(fid) && fid != QbIds.RECORD_ID_FID) throw QbApiException.BadRequest($"Unknown field with Id \"{fid}\".");
        if (select.Count == 0)
            select = fields.Where(f => f.Fid.HasValue && !f.IsDeleted && visible.Contains(f.Fid.Value)).Select(f => (long)f.Fid!.Value)
                .Distinct().OrderBy(f => f).ToList();
        var returned = select.Where(visible.Contains).ToList(); // a hidden field is left out, as in Quickbase

        var sort = SortOf(request, byFid);
        foreach (var s in sort)
            if (!byFid.ContainsKey(s.FieldId) && s.FieldId != QbIds.RECORD_ID_FID) throw QbApiException.BadRequest($"Unknown field with Id \"{s.FieldId}\".");

        var knownFids = fields.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToHashSet();
        FilterGroup? where = string.IsNullOrWhiteSpace(request.Where) ? null : QbWhereParser.Parse(request.Where, knownFids);
        var filter = new FilterGroup { Logic = "AND" };
        if (where is not null) filter.Nodes.Add(new FilterNode { Group = where });
        if (access.ViewFilter is { } vf) filter.Nodes.Add(new FilterNode { Group = vf });
        var filterTree = filter.Nodes.Count > 0 ? filter : null;

        if (!_appEncrypted.TryGetValue(table.AppId, out var appEncrypted))
            _appEncrypted[table.AppId] = appEncrypted = (await _apps.GetByIdAsync(table.AppId, ct)).IsEncrypted;
        bool Computed(long fid) => byFid.TryGetValue(fid, out var f) && (PhysicalNaming.IsComputedTypeCode(f.TypeCode));
        bool Encrypted(long fid) => byFid.TryGetValue(fid, out var f) && EncryptedRowFilter.IsEncrypted(f, appEncrypted);
        var filterFids = FilterFids(filterTree).ToList();
        var inMemory = EncryptedRowFilter.TouchesEncrypted(filterTree, fields, appEncrypted)
                       || filterFids.Any(Computed) || sort.Any(s => Computed(s.FieldId) || Encrypted(s.FieldId));

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
                    computed.Add(values);
                }
                var matches = filterTree is null ? null : EncryptedRowFilter.MatchingIds(rows, fields, filterTree, computed).ToHashSet();
                for (var i = 0; i < rows.Count; i++)
                {
                    var key = RowKey(rows[i]);
                    if (matches is not null && !matches.Contains(key.Rid)) continue;
                    var slim = new Dictionary<string, object?> { ["Id"] = key.Rid, ["PublicId"] = key.PublicId };
                    var all = Merge(EncryptedRowFilter.ValuesByFid(rows[i], fields), computed[i], rows[i]);
                    kept.Add((slim, sort.Count == 0 ? new Dictionary<long, object?>() : sort.ToDictionary(s => s.FieldId, s => all.GetValueOrDefault(s.FieldId))));
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
        var fileBase = $"/apps/{await _apps.GetPublicIdByIdAsync(table.AppId, ct)}/files/download";
        var fids = returned.Select(f => (int)f).ToList();
        var data = new List<IDictionary<string, QbCell>>(page.Count);
        foreach (var (rid, publicId) in page)
            data.Add(await ReadFieldsAsync(tableId, rid, publicId, fids, byFid, users.GetValueOrDefault(publicId), fileBase, refTypes, ct));

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
            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
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
