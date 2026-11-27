using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// API_DoQuery and API_DoQueryCount. Records are read through <see cref="QbRecordService.QueryAsync(QbQueryRequest, CancellationToken)"/>
/// (same permissions / filters as the REST query) and written in Quickbase's XML layout, measured on a live realm:
/// default format (field labels as element names, <c>&lt;dbinfo&gt;</c>, <c>&lt;variables&gt;</c>, <c>&lt;chdbids&gt;</c>) and
/// <c>fmt=structured</c> (table schema + <c>&lt;f id&gt;</c> cells), CRLF line ends, Quickbase's own indentation.
/// </summary>
public sealed class QbXmlQueryActions
{
    public const int NO_SUCH_QUERY = 33;

    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IAppRepository _apps;
    private readonly IAppVariableRepository _variables;
    private readonly IReportRepository _reports;
    private readonly IAppAccessService _access;
    private readonly QbRecordService _records;
    private readonly QbReportService _reportService;
    private readonly QbIdResolver _ids;
    private readonly QbReferenceTypes _referenceTypes;
    private readonly IHttpContextAccessor _http;
    private readonly IRecordRepository _recordRows;

    public QbXmlQueryActions(IAppTableRepository tables, IAppFieldRepository fields, IAppRepository apps, IAppVariableRepository variables,
        IReportRepository reports, IAppAccessService access, QbRecordService records, QbReportService reportService, QbIdResolver ids,
        QbReferenceTypes referenceTypes, IHttpContextAccessor http, IRecordRepository recordRows)
    {
        _recordRows = recordRows;
        _tables = tables;
        _fields = fields;
        _apps = apps;
        _variables = variables;
        _reports = reports;
        _access = access;
        _records = records;
        _reportService = reportService;
        _ids = ids;
        _referenceTypes = referenceTypes;
        _http = http;
    }

    // ---------------------------------------------------------------- request parsing

    /// <summary>The <c>options</c> parameter: <c>num-n.skip-n.sortorder-A|D….onlynew.nosort</c>.</summary>
    public sealed record QueryOptions(int? Num, int Skip, string SortOrder, bool OnlyNew, bool NoSort);

    public static QueryOptions ParseOptions(string? options)
    {
        int? num = null; var skip = 0; var order = ""; bool onlyNew = false, noSort = false;
        foreach (var raw in (options ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var o = raw.ToLowerInvariant();
            if (o.StartsWith("num-") && int.TryParse(o[4..], out var n) && n >= 0) num = n;
            else if (o.StartsWith("skip-") && int.TryParse(o[5..], out var k) && k >= 0) skip = k;
            else if (o.StartsWith("sortorder-")) order = raw[10..].Replace("-", "").ToUpperInvariant();
            else if (o == "onlynew") onlyNew = true;
            else if (o == "nosort") noSort = true;
        }
        return new QueryOptions(num, skip, order, onlyNew, noSort);
    }

    /// <summary>A '.'-separated fid list (clist / slist); entries that are not numbers are dropped.</summary>
    public static List<long> ParseFidList(string? list) =>
        (list ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.TryParse(x, out var f) ? f : -1).Where(f => f > 0).ToList();

    /// <summary>Quickbase's element name for a field label: lower case, every other character than a-z / 0-9 becomes '_'
    /// ("Record ID#" → record_id_, "Parent - T Text" → parent___t_text); a leading digit gets a '_' prefix.</summary>
    public static string ElementName(string label)
    {
        var sb = new StringBuilder(label.Length + 1);
        foreach (var c in label.ToLowerInvariant()) sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        var name = sb.ToString();
        return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name;
    }

    private sealed record Plan(AppTable Table, IReadOnlyList<AppField> Fields, List<long> Columns, JsonElement? Sort, string? Where,
        bool Empty, long Qid, string QName, bool OnlyNewReport = false);

    private async Task<(AppTable Table, IReadOnlyList<AppField> Fields)> LoadTableAsync(string dbid, CancellationToken ct)
    {
        Guid tableId;
        try { tableId = await _ids.TableAsync(dbid, "dbid", ct); }
        catch (QbApiException) { throw QbXmlErrors.UnknownDbid(); }
        AppTable table;
        try { table = await _tables.GetByPublicIdAsync(tableId, ct); }
        catch (Domain.Exceptions.NotFoundException) { throw QbXmlErrors.UnknownDbid(); }
        await _access.RequireMembershipByTablePublicIdAsync(tableId, ct);
        return (table, await _fields.ListByTableAsync(table.Id, ct));
    }

    /// <summary>
    /// Quickbase's query number for each report of the table (live realm, 98 tables): the table's own List All is 1 and List
    /// Changes 2, 3 and 4 are never shown, and every other report gets 5, 6, 7… in the order it was made; a deleted report keeps
    /// its number (it is never reused), so numbers do not move. The internal "Default Report Settings" record has none.
    /// </summary>
    private async Task<Dictionary<Guid, long>> QueryNumbersAsync(AppTable table, CancellationToken ct) =>
        QueryNumbers(await _reports.ListByTableIncludingDeletedAsync(table.Id, ct));

    public static Dictionary<Guid, long> QueryNumbers(IEnumerable<Report> allReportsOfTable)
    {
        var rows = allReportsOfTable.Where(r => !r.IsDefaultSettingsRecord).OrderBy(r => r.Id).ToList();
        var numbers = new Dictionary<Guid, long>();
        var listAll = rows.FirstOrDefault(r => string.Equals(r.Name, "List All", StringComparison.OrdinalIgnoreCase));
        var listChanges = rows.FirstOrDefault(r => string.Equals(r.Name, "List Changes", StringComparison.OrdinalIgnoreCase));
        if (listAll is not null) numbers[listAll.PublicId] = 1;
        if (listChanges is not null) numbers[listChanges.PublicId] = 2;
        long next = 5;
        foreach (var r in rows)
            if (!numbers.ContainsKey(r.PublicId)) numbers[r.PublicId] = next++;
        return numbers;
    }

    /// <summary>The report a Quickbase query number names (deleted reports name nothing).</summary>
    private async Task<Guid?> ReportByNumberAsync(AppTable table, long number, CancellationToken ct)
    {
        var all = await _reports.ListByTableIncludingDeletedAsync(table.Id, ct);
        var hit = QueryNumbers(all).FirstOrDefault(p => p.Value == number);
        return hit.Value == number && all.FirstOrDefault(r => r.PublicId == hit.Key) is { IsDeleted: false } r ? r.PublicId : null;
    }

    /// <summary>Quickbase's <c>&lt;qyopts&gt;</c>: "so-" one letter per sort field, "onlynew." for a report sorted on Date Modified
    /// newest first (Quickbase's List Changes), then always "nos.qws." (live realm: "nos.qws." / "so-D.onlynew.nos.qws.").</summary>
    public static string Qyopts(IReadOnlyList<QbSortBy> sort)
    {
        var sb = new StringBuilder();
        if (sort.Count > 0) sb.Append("so-").Append(string.Concat(sort.Select(x => x.Order == "DESC" ? 'D' : 'A'))).Append('.');
        if (sort.Count > 0 && sort[0].FieldId == QbIds.DATE_MODIFIED_FID && sort[0].Order == "DESC") sb.Append("onlynew.");
        return sb.Append("nos.qws.").ToString();
    }

    private static QbXmlException NoQuery(string what, bool byName = false) => new(NO_SUCH_QUERY, "No such query",
        $"Sorry, there is no query in this application table with the query {(byName ? "name (qname)" : "identifier (qid)")} of \"{what}\".");

    private async Task<Plan> PlanAsync(QbXmlRequest req, CancellationToken ct)
    {
        var (table, fields) = await LoadTableAsync(req.DbId, ct);
        var live = fields.Where(f => f.Fid.HasValue && !f.IsDeleted).ToList();
        var known = live.Select(f => (long)f.Fid!.Value).ToHashSet();
        var opts = ParseOptions(req.Get("options"));

        // qid / qname: the saved report's filter, columns and sort. A qid wins over a query (live realm).
        QbReportResponse? report = null;
        long qid = -1;
        var qname = "";
        if (!string.IsNullOrWhiteSpace(req.Get("qid")))
        {
            var raw = req.Get("qid")!.Trim();
            // A number is the table's query number (1, 2, 3…, as in Quickbase); a Guid is the report's own id.
            Guid reportId;
            if (long.TryParse(raw, out var n))
                reportId = await ReportByNumberAsync(table, n, ct) ?? throw NoQuery(raw);
            else
            {
                try { reportId = await _ids.ReportAsync(raw, table.PublicId, "qid", ct); }
                catch (QbApiException) { throw NoQuery(raw); }
            }
            try { report = await _reportService.GetReportAsync(reportId, table.PublicId, ct); }
            catch (QbApiException) { throw NoQuery(raw); }
            qid = (await QueryNumbersAsync(table, ct)).GetValueOrDefault(reportId, -1);
            qname = report.Name;
        }
        else if (!string.IsNullOrWhiteSpace(req.Get("qname")))
        {
            var name = req.Get("qname")!.Trim();
            var match = (await _reports.ListByTableAsync(table.PublicId, ct))
                .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw NoQuery(name, byName: true);
            report = await _reportService.GetReportAsync(match.PublicId, table.PublicId, ct);
            qid = (await QueryNumbersAsync(table, ct)).GetValueOrDefault(match.PublicId, -1);
            qname = match.Name;
        }

        // clist: given order, unknown ids ignored; "a" = every field (built-ins 1-5 last); none = the report's / default columns.
        var clistRaw = req.Get("clist")?.Trim();
        List<long> columns;
        if (string.Equals(clistRaw, "a", StringComparison.OrdinalIgnoreCase))
            columns = live.Where(f => !QbFieldShape.IsSystem(f)).Select(f => (long)f.Fid!.Value).Distinct().OrderBy(f => f)
                .Concat(live.Where(QbFieldShape.IsSystem).Select(f => (long)f.Fid!.Value).Distinct().OrderBy(f => f)).ToList();
        else if (!string.IsNullOrWhiteSpace(clistRaw))
            columns = ParseFidList(clistRaw).Where(known.Contains).Distinct().ToList();
        else if (report?.Query.Fields is { Count: > 0 } rf)
            columns = rf.Where(known.Contains).ToList();
        else
            columns = live.Where(QbFieldShape.AppearsByDefault).Select(f => (long)f.Fid!.Value).Distinct().OrderBy(f => f).ToList();

        // Sort: slist (+ sortorder letters per field), else the report's sort, else the table's default report sort.
        var sort = new List<object>();
        if (!opts.NoSort)
        {
            var slist = ParseFidList(req.Get("slist")).Where(known.Contains).ToList();
            if (slist.Count > 0)
                for (var i = 0; i < slist.Count; i++)
                {
                    var letter = i < opts.SortOrder.Length ? opts.SortOrder[i] : opts.SortOrder.Length > 0 ? opts.SortOrder[^1] : 'A';
                    sort.Add(new { fieldId = slist[i], order = letter == 'D' ? "DESC" : "ASC" });
                }
            else
            {
                var source = report;
                if (source is null && await _reports.GetDefaultByTableAsync(table.PublicId, ct) is { } def)
                {
                    try { source = await _reportService.GetReportAsync(def.PublicId, table.PublicId, ct); } catch (QbApiException) { }
                }
                foreach (var s in source?.Query.SortBy ?? Array.Empty<QbSortBy>())
                    if (known.Contains(s.FieldId)) sort.Add(new { fieldId = s.FieldId, order = s.Order == "DESC" ? "DESC" : "ASC" });
            }
        }
        JsonElement? sortEl = sort.Count > 0 ? JsonSerializer.SerializeToElement(sort) : null;

        var where = report is not null ? report.Query.Filter : req.Get("query");
        if (string.IsNullOrWhiteSpace(where)) where = null;
        // Quickbase answers a query it cannot read with no records instead of an error (live realm).
        // A report marked "only new or changed records" (Quickbase's List Changes; ours: sorted on Date Modified newest first —
        // see Qyopts) gives an API caller no records (live realm: qid=2 → none).
        var empty = opts.OnlyNew;
        // API_DoQueryCount still counts every match of such a report (live realm: qid=2 → numMatches 7).
        var onlyNewReport = report?.Query.SortBy is { } rs && Qyopts(rs).Contains("onlynew.", StringComparison.Ordinal);
        if (where is not null)
        {
            try { QbWhereParser.Parse(where, known.Select(f => (int)f).ToHashSet()); }
            catch (QbApiException) { empty = true; }
        }
        return new Plan(table, fields, columns, sortEl, where, empty, qid, qname, onlyNewReport);
    }

    // ---------------------------------------------------------------- API_DoQueryCount

    public async Task<QbXmlResponse> DoQueryCountAsync(QbXmlRequest req, CancellationToken ct)
    {
        var plan = await PlanAsync(req, ct);
        var count = 0;
        if (!plan.Empty)
        {
            var result = await _records.QueryAsync(new QbQueryRequest
            {
                From = plan.Table.PublicId.ToString(),
                Select = new List<JsonElement> { JsonSerializer.SerializeToElement(QbIds.RECORD_ID_FID) },
                Where = plan.Where,
                SortBy = JsonSerializer.SerializeToElement(false),
                Options = new QbQueryOptions { Top = 1 },
            }, ct);
            count = result.Metadata.TotalRecords;
        }
        return new QbXmlResponse(req.Action) { Raw = Header(req.Action, req.Udata) + $"\t<numMatches>{count}</numMatches>{CRLF}</qdbapi>{CRLF}" };
    }

    // ---------------------------------------------------------------- API_DoQuery

    private sealed record Row(long Rid, long UpdateId, IDictionary<string, QbCell> Cells, Dictionary<string, object?> Raw);

    public async Task<QbXmlResponse> DoQueryAsync(QbXmlRequest req, CancellationToken ct)
    {
        var plan = await PlanAsync(req, ct);
        var opts = ParseOptions(req.Get("options"));
        var structured = string.Equals(req.Get("fmt")?.Trim(), "structured", StringComparison.OrdinalIgnoreCase);
        var useFids = req.Flag("useFids");
        var includeRids = req.Flag("includeRids");
        var byFid = plan.Fields.Where(f => f.Fid.HasValue && !f.IsDeleted).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var rows = new List<Row>();
        var returned = plan.Columns;
        if (!plan.Empty && !plan.OnlyNewReport && plan.Columns.Count > 0)
        {
            var wanted = opts.Num;
            var skip = opts.Skip;
            while (wanted is null or > 0)
            {
                var page = Math.Min(wanted ?? QbReportService.MAX_TOP, QbReportService.MAX_TOP);
                var meta = new Dictionary<long, Application.Records.RecordResult>();
                var result = await _records.QueryAsync(new QbQueryRequest
                {
                    From = plan.Table.PublicId.ToString(),
                    Select = plan.Columns.Prepend(QbIds.RECORD_ID_FID).Distinct().Select(f => JsonSerializer.SerializeToElement(f)).ToList(),
                    Where = plan.Where,
                    SortBy = plan.Sort ?? JsonSerializer.SerializeToElement(false),
                    Options = new QbQueryOptions { Skip = skip, Top = page },
                }, ct, (rid, record) => meta[rid] = record);
                returned = result.Fields.Select(f => f.Id).ToList(); // fields hidden from the caller's role are left out
                foreach (var cells in result.Data)
                {
                    var rid = Convert.ToInt64(cells["3"].Value, CultureInfo.InvariantCulture);
                    var rec = meta[rid];
                    var stamp = rec.ModifiedOn ?? rec.CreatedOn;
                    rows.Add(new Row(rid, new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), cells, rec.Fields));
                }
                if (result.Data.Count < page) break;
                skip += page;
                if (wanted is not null) wanted -= page;
            }
            // Record ID# is always read (to key the row); drop it again when the caller did not ask for it.
        }
        returned = returned.Where(plan.Columns.Contains).OrderBy(plan.Columns.IndexOf).ToList();

        var refTypes = await _referenceTypes.ForAsync(plan.Fields, ct);
        var app = await _apps.GetByIdAsync(plan.Table.AppId, ct);
        var variables = await _variables.ListAsync(plan.Table.AppId, ct);
        var cols = new List<Col>();
        foreach (var fid in returned)
        {
            if (!byFid.TryGetValue(fid, out var field))
            {
                cols.Add(new Col(fid, "Record ID#", "recordid", null, null));
                continue;
            }
            var type = QbFieldPropertiesBuilder.TypeOf(field, refTypes);
            var ctx = await _referenceTypes.ContextAsync(field, plan.Fields, ct);
            var props = QbFieldShape.Properties(field, type, ctx);
            cols.Add(new Col(fid, string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label!, type.FieldType,
                props.TryGetValue("decimalPlaces", out var dp) && dp is not null ? Convert.ToInt32(dp, CultureInfo.InvariantCulture) : null, field));
        }

        var origin = _http.HttpContext?.Request is { } r ? $"{r.Scheme}://{r.Host}" : "";
        var sb = new StringBuilder(Header(req.Action, req.Udata));
        if (!structured)
        {
            sb.Append("<dbinfo>").Append(CRLF)
              .Append("<name>").Append(Esc(plan.Table.Name)).Append("</name>").Append(CRLF)
              .Append("<desc>").Append(Esc(plan.Table.Description ?? "")).Append("</desc>").Append(CRLF)
              .Append("</dbinfo>").Append(CRLF)
              .Append("<variables>").Append(CRLF);
            foreach (var v in variables.Where(v => !v.IsDeleted))
            {
                var tag = ElementName(v.Name);
                sb.Append('<').Append(tag).Append('>').Append(Esc(v.Value)).Append("</").Append(tag).Append('>').Append(CRLF);
            }
            sb.Append("</variables>").Append(CRLF).Append("<chdbids>").Append(CRLF).Append("</chdbids>").Append(CRLF);
            foreach (var row in rows)
            {
                sb.Append(includeRids ? $"  <record rid=\"{row.Rid}\">" : "  <record>").Append(CRLF);
                foreach (var c in cols)
                {
                    var text = CellText(c, row, structured: false, origin);
                    var tag = useFids ? null : ElementName(c.Label);
                    Cell(sb, tag, c.Fid, text);
                    if (!useFids && c.Type == "address" && string.Equals(req.Get("clist")?.Trim(), "a", StringComparison.OrdinalIgnoreCase))
                        foreach (var (suffix, value) in AddressParts(row.Raw.GetValueOrDefault(c.Fid.ToString())))
                            Cell(sb, tag + suffix, c.Fid, Esc(value));
                }
                sb.Append("    <update_id>").Append(row.UpdateId).Append("</update_id>").Append(CRLF).Append("  </record>").Append(CRLF);
            }
            sb.Append("</qdbapi>").Append(CRLF);
            return new QbXmlResponse(req.Action) { Raw = sb.ToString() };
        }

        // ---- fmt=structured
        var reports = await _reports.ListByTableAsync(plan.Table.PublicId, ct);
        var queryNumbers = await QueryNumbersAsync(plan.Table, ct);
        long QueryNumber(Report r) => queryNumbers.GetValueOrDefault(r.PublicId, -1);
        var lastFid = byFid.Keys.DefaultIfEmpty(0).Max();
        var defSort = await DefaultSortAsync(plan.Table, ct) ?? new QbSortBy(QbIds.RECORD_ID_FID, "ASC"); // no default: Record ID# order
        // Quickbase's next record id is its counter: deleted records still count (live realm: 54 after test rows were deleted).
        var nextRid = await _recordRows.GetNextRecordIdAsync(plan.Table, ct);
        sb.Append("    <qid>").Append(plan.Qid).Append("</qid>").Append(CRLF)
          .Append("    <qname>").Append(Esc(plan.QName)).Append("</qname>").Append(CRLF)
          .Append("    <table>").Append(CRLF)
          .Append("      <name>").Append(Esc(plan.Table.Name)).Append("</name>").Append(CRLF)
          .Append("      <desc>").Append(Esc(plan.Table.Description ?? "")).Append("</desc>").Append(CRLF)
          .Append("      <original>").Append(CRLF)
          .Append("        <table_id>").Append(QbIds.Format(plan.Table.PublicId)).Append("</table_id>").Append(CRLF)
          .Append("\t\t <app_id>").Append(QbIds.Format(app.PublicId)).Append("</app_id>").Append(CRLF)
          .Append("        <cre_date>").Append(Ms(plan.Table.CreatedOn)).Append("</cre_date>").Append(CRLF)
          .Append("        <mod_date>").Append(Ms(plan.Table.ModifiedOn ?? plan.Table.CreatedOn)).Append("</mod_date>").Append(CRLF)
          .Append("        <next_record_id>").Append(nextRid).Append("</next_record_id>").Append(CRLF)
          .Append("        <next_field_id>").Append(lastFid + 1).Append("</next_field_id>").Append(CRLF)
          .Append("        <next_query_id>").Append(queryNumbers.Values.DefaultIfEmpty(4).Max() < 4 ? 5 : queryNumbers.Values.DefaultIfEmpty(4).Max() + 1).Append("</next_query_id>").Append(CRLF)
          .Append("        <def_sort_fid>").Append(defSort.FieldId.ToString(CultureInfo.InvariantCulture)).Append("</def_sort_fid>").Append(CRLF)
          .Append("        <def_sort_order>").Append(defSort.Order == "DESC" ? "0" : "1").Append("</def_sort_order>").Append(CRLF)
          .Append("    <key_fid>").Append(QbRecordService.KeyFid(plan.Table, plan.Fields)).Append("</key_fid>").Append(CRLF)
          .Append("        <single_record_name>").Append(Esc(plan.Table.SingularLabel ?? "")).Append("</single_record_name>").Append(CRLF)
          .Append("        <plural_record_name>").Append(Esc(plan.Table.PluralLabel ?? "")).Append("</plural_record_name>").Append(CRLF)
          .Append("      </original>").Append(CRLF)
          .Append("      <variables>").Append(CRLF);
        foreach (var v in variables.Where(v => !v.IsDeleted))
            sb.Append("        <var name=\"").Append(Esc(v.Name)).Append("\">").Append(Esc(v.Value)).Append("</var>").Append(CRLF);
        sb.Append("      </variables>").Append(CRLF).Append("      <queries>").Append(CRLF);
        foreach (var rep in reports)
        {
            sb.Append("        <query id=\"").Append(QueryNumber(rep)).Append("\">").Append(CRLF)
              .Append("          <qyname>").Append(Esc(rep.Name)).Append("</qyname>").Append(CRLF)
              .Append("          <qytype>").Append(rep.ReportType.ToLowerInvariant()).Append("</qytype>").Append(CRLF);
            if (!string.IsNullOrWhiteSpace(rep.Description))
                sb.Append("          <qydesc>").Append(Esc(rep.Description!)).Append("</qydesc>").Append(CRLF);
            // The report's sort: <qyslst> fids and <qyopts> "so-" one letter per field (live realm: List Changes = 2 / so-D.).
            IReadOnlyList<QbSortBy> repSort = Array.Empty<QbSortBy>();
            // The table's default report sorts by the table's default sort, which Quickbase does not repeat in the query
            // (live realm: List All sorted 6 DESC → <qyopts>nos.qws.</qyopts>, no <qyslst>).
            if (!rep.IsDefault)
                try { repSort = (await _reportService.GetReportAsync(rep.PublicId, plan.Table.PublicId, ct)).Query.SortBy ?? repSort; }
            catch (QbApiException) { }
            if (repSort.Count > 0)
                sb.Append("          <qyslst>").Append(string.Join(".", repSort.Select(x => x.FieldId))).Append("</qyslst>").Append(CRLF);
            sb.Append("          <qyopts>").Append(Qyopts(repSort)).Append("</qyopts>").Append(CRLF);
            sb.Append("\t\t  <qycalst>0.0</qycalst>").Append(CRLF).Append("        </query>").Append(CRLF);
        }
        sb.Append("      </queries>").Append(CRLF).Append("      <fields>").Append(CRLF);
        foreach (var c in cols.Where(c => c.Field is not null))
        {
            var el = QbFieldShape.XmlField(c.Field!, QbFieldPropertiesBuilder.TypeOf(c.Field!, refTypes), await _referenceTypes.ContextAsync(c.Field!, plan.Fields, ct));
            sb.Append("        <field");
            // The schema part names the built-in fields' role (live realm), right after base_type.
            var role = c.Fid switch { 1 => "created", 2 => "modified", 3 => "recordid", 4 => "owner", 5 => "modifier", _ => null };
            foreach (var a in el.Attributes())
            {
                if (a.Name.LocalName == "mode" && role is not null) { sb.Append(" role=\"").Append(role).Append('"'); role = null; }
                sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(Esc(a.Value)).Append('"');
                if (a.Name.LocalName == "base_type" && role is not null && el.Attribute("mode") is null)
                {
                    sb.Append(" role=\"").Append(role).Append('"');
                    role = null;
                }
            }
            sb.Append('>').Append(CRLF);
            foreach (var child in el.Elements())
            {
                var indent = child.Name.LocalName is "comma_start" or "numberfmt" ? "\t\t  " : "          ";
                if (child.HasElements)
                {
                    // Nested lists (choices …) as Quickbase writes them: one element per line, two more spaces each level.
                    sb.Append(indent).Append('<').Append(child.Name.LocalName);
                    foreach (var a in child.Attributes()) sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(Esc(a.Value)).Append('"');
                    sb.Append(">\n");
                    foreach (var g in child.Elements())
                        sb.Append(indent).Append("  ").Append(SchemaLeaf(g)).Append('\n');
                    sb.Append(indent).Append("</").Append(child.Name.LocalName).Append(">\n");
                }
                else
                    sb.Append(indent).Append(SchemaLeaf(child)).Append('\n');
            }
            sb.Append("        </field>").Append(CRLF);
        }
        sb.Append("      </fields>").Append(CRLF).Append("<lastluserid>0</lastluserid>").Append(CRLF);

        var userCols = cols.Where(c => c.Type is "user" or "multiuser").ToList();
        if (userCols.Count > 0)
        {
            var users = new List<(string Id, string Name)>();
            var anyEmpty = false;
            foreach (var row in rows)
                foreach (var c in userCols)
                {
                    var list = UserValues(row.Cells.TryGetValue(c.Fid.ToString(), out var cell) ? cell.Value : null);
                    if (list.Count == 0) anyEmpty = true;
                    foreach (var u in list)
                        if (u.Id.Length > 0 && users.All(x => x.Id != u.Id)) users.Add(u);
                }
            sb.Append("<lusers>").Append(CRLF);
            if (anyEmpty) sb.Append("<luser id=\"-2147483648\"></luser>").Append(CRLF);
            foreach (var (id, name) in users) sb.Append("<luser id=\"").Append(Esc(id)).Append("\">").Append(Esc(name)).Append("</luser>").Append(CRLF);
            sb.Append("</lusers>").Append(CRLF);
        }

        sb.Append("      <records>").Append(CRLF);
        foreach (var row in rows)
        {
            sb.Append(includeRids ? $"        <record rid=\"{row.Rid}\">" : "        <record>").Append(CRLF);
            foreach (var c in cols)
            {
                var text = CellText(c, row, structured: true, origin);
                sb.Append("          ");
                Cell(sb, null, c.Fid, text);
            }
            sb.Append("          <update_id>").Append(row.UpdateId).Append("</update_id>").Append(CRLF).Append("        </record>").Append(CRLF);
        }
        sb.Append("      </records>").Append(CRLF).Append("    </table>").Append(CRLF).Append("</qdbapi>").Append(CRLF);
        return new QbXmlResponse(req.Action) { Raw = sb.ToString() };
    }

    private async Task<QbSortBy?> DefaultSortAsync(AppTable table, CancellationToken ct)
    {
        if (await _reports.GetDefaultByTableAsync(table.PublicId, ct) is not { } def) return null;
        try { return (await _reportService.GetReportAsync(def.PublicId, table.PublicId, ct)).Query.SortBy?.FirstOrDefault(); }
        catch (QbApiException) { return null; }
    }

    // ---------------------------------------------------------------- values

    private sealed record Col(long Fid, string Label, string Type, int? Decimals, AppField? Field);

    public const string CRLF = "\r\n";

    /// <summary>One schema element as Quickbase writes it: text escaped with quotes as &amp;quot;, an empty element as &lt;x /&gt;.</summary>
    private static string SchemaLeaf(XElement e)
    {
        if (e.HasElements) return e.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
        var attrs = string.Concat(e.Attributes().Select(a => $" {a.Name.LocalName}=\"{Esc(a.Value)}\""));
        return e.IsEmpty ? $"<{e.Name.LocalName}{attrs} />" : $"<{e.Name.LocalName}{attrs}>{Esc(e.Value)}</{e.Name.LocalName}>";
    }

    /// <summary>These actions answer errors with Quickbase's line ends too (CRLF everywhere but after &lt;qdbapi&gt;).</summary>
    public static bool UsesCrlf(string action) => action.ToUpperInvariant() is "API_DOQUERY" or "API_DOQUERYCOUNT";

    public static string ToCrlf(string xml) => xml.Replace("\r\n", "\n").Replace("\n", CRLF).Replace("<qdbapi>" + CRLF, "<qdbapi>\n");

    /// <summary>The response head exactly as Quickbase writes it ("&lt;qdbapi&gt;" ends with LF, the rest with CRLF).</summary>
    public static string Header(string action, string? udata)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n");
        sb.Append("\t<action>").Append(Esc(action)).Append("</action>").Append(CRLF)
          .Append("\t<errcode>0</errcode>").Append(CRLF)
          .Append("\t<errtext>No error</errtext>").Append(CRLF);
        if (udata is not null) sb.Append("\t<udata>").Append(Esc(udata)).Append("</udata>").Append(CRLF);
        return sb.ToString();
    }

    private static void Cell(StringBuilder sb, string? tag, long fid, string text)
    {
        var open = tag is null ? $"<f id=\"{fid}\"" : "<" + tag;
        if (text.Length == 0) sb.Append(open).Append("/>").Append(CRLF);
        else sb.Append(open).Append('>').Append(text).Append(tag is null ? "</f>" : "</" + tag + ">").Append(CRLF);
    }

    /// <summary>XML text escaping as Quickbase does it (quotes in text as &amp;quot;), minus characters XML cannot carry.</summary>
    public static string Esc(string value) =>
        QbXmlResponse.Clean(value).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static long Ms(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)).ToUnixTimeMilliseconds();

    /// <summary>Already-escaped XML content of one cell.</summary>
    private static string CellText(Col c, Row row, bool structured, string origin)
    {
        if (c.Type == "dblink") return row.Rid.ToString(CultureInfo.InvariantCulture); // a report link carries the record's id
        var value = row.Cells.TryGetValue(c.Fid.ToString(), out var cell) ? cell.Value : null;
        if (c.Type == "file") return FileText(value, structured, origin);
        if (c.Fid == QbIds.DATE_MODIFIED_FID) return row.UpdateId.ToString(CultureInfo.InvariantCulture); // never empty; = update_id
        // Timestamps keep their milliseconds (the REST shape stops at seconds): read the stored value.
        if (c.Type == "timestamp" && row.Raw.GetValueOrDefault(c.Fid.ToString()) is { } stored && AsInstant(stored) is { } instant)
            return Ms(instant).ToString(CultureInfo.InvariantCulture);
        if (c.Type is "user" or "multiuser")
            return Esc(string.Join(";", UserValues(value).Select(u => structured ? u.Id : u.Name)));
        return XmlValue(value, c.Type, c.Decimals);
    }

    /// <summary>
    /// A REST-shaped value (<see cref="QbValueFormatter"/>) in Quickbase's XML form: dates / timestamps / times of day as
    /// milliseconds, durations in milliseconds, checkboxes 1/0, numbers with the field's decimal places (a percent as a fraction
    /// with two more), multi-select text joined by ';', and line breaks in text as &lt;BR/&gt;.
    /// </summary>
    public static string XmlValue(object? value, string type, int? decimals)
    {
        switch (value)
        {
            case null:
                return type == "checkbox" ? "0" : "";
            case bool b:
                return b ? "1" : "0";
            case JsonElement je:
                return XmlValue(je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => je.GetRawText(),
                }, type, decimals);
            case System.Collections.IEnumerable list and not string:
                return Esc(string.Join(";", list.Cast<object?>().Select(x => x?.ToString() ?? "")));
        }
        switch (type)
        {
            case "numeric" or "currency" or "percent" or "rating" or "recordid":
                if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                    return Esc(value.ToString() ?? "");
                if (type == "recordid") return decimal.Truncate(n).ToString(CultureInfo.InvariantCulture);
                if (decimals is { } dp) return n.ToString("F" + (type == "percent" ? dp + 2 : dp), CultureInfo.InvariantCulture);
                return (n / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
            case "duration":
                return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var ms)
                    ? decimal.Round(ms).ToString(CultureInfo.InvariantCulture) : "";
            case "date" or "timestamp":
                return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                    ? Ms(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToString(CultureInfo.InvariantCulture) : Esc(value.ToString() ?? "");
            case "timeofday":
                return TimeSpan.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var t)
                    ? ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : Esc(value.ToString() ?? "");
            default:
                var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "";
                return Esc(text.Replace("\r\n", "\n")).Replace("\n", "<BR/>\n");
        }
    }

    private static DateTime? AsInstant(object value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.UtcDateTime,
        string t when t.Length >= 10 && char.IsDigit(t[0]) && t.Contains('-') && DateTime.TryParse(t, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p) => p,
        _ => null,
    };

    private static List<(string Id, string Name)> UserValues(object? value)
    {
        var result = new List<(string, string)>();
        void Add(object? u)
        {
            switch (u)
            {
                case IDictionary<string, object?> map:
                    var id = map.TryGetValue("id", out var i) ? i?.ToString() ?? "" : "";
                    var name = map.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                    if (id.Length > 0 || name.Length > 0) result.Add((id, name.Length > 0 ? name : id));
                    break;
                case string s when s.Length > 0:
                    result.Add((s, s));
                    break;
            }
        }
        if (value is System.Collections.IEnumerable list and not string and not IDictionary<string, object?>)
            foreach (var u in list) Add(u);
        else Add(value);
        return result;
    }

    /// <summary>Default format: the file name; structured: the name followed by <c>&lt;url&gt;</c>.</summary>
    private static string FileText(object? value, bool structured, string origin)
    {
        if (value is not IDictionary<string, object?> file) return "";
        var versions = file.TryGetValue("versions", out var v) && v is System.Collections.IEnumerable vl ? vl.Cast<object?>().ToList() : new List<object?>();
        var name = versions.LastOrDefault() is IDictionary<string, object?> last && last.TryGetValue("fileName", out var fn) ? fn?.ToString() ?? "" : "";
        if (name.Length == 0) return "";
        var url = file.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
        if (url.StartsWith('/')) url = origin + url;
        return structured && url.Length > 0 ? $"{Esc(name)}<url>{Esc(url)}</url>" : Esc(name);
    }

    /// <summary>clist=a writes an address's parts too (live realm); an empty country reads "United States".</summary>
    private static IEnumerable<(string Suffix, string Value)> AddressParts(object? raw)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = raw is JsonElement je ? je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText() : raw?.ToString();
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) parts[p.Name] = p.Value.GetString()!.Trim();
            }
            catch (JsonException) { }
        }
        string P(string k) => parts.TryGetValue(k, out var x) ? x : "";
        yield return ("__street_1", P("street1"));
        yield return ("__street_2", P("street2"));
        yield return ("__city", P("city"));
        yield return ("__state_region", P("state"));
        yield return ("__postal_code", P("zip"));
        yield return ("__country", P("country") is { Length: > 0 } c ? c : "United States");
    }
}
