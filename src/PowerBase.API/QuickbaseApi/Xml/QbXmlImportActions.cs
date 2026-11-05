using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>API_ImportFromCSV, API_RunImport and API_GenAddRecordForm.</summary>
public sealed class QbXmlImportActions
{
    /// <summary>How long API_RunImport waits for the background run to finish (user decision: wait until done).</summary>
    public static readonly TimeSpan RUN_IMPORT_TIMEOUT = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(1);

    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IAppRepository _apps;
    private readonly IImportDefinitionRepository _definitions;
    private readonly IAppAccessService _access;
    private readonly IConfiguration _configuration;
    private readonly QbXmlRecordActions _recordActions;
    private readonly StartImportRunHandler _startImport;
    private readonly GetImportRunStatusHandler _importStatus;
    private readonly QbIdResolver _ids;

    public QbXmlImportActions(IAppTableRepository tables, IAppFieldRepository fields, IAppRepository apps, IImportDefinitionRepository definitions,
        IAppAccessService access, IConfiguration configuration, QbXmlRecordActions recordActions,
        StartImportRunHandler startImport, GetImportRunStatusHandler importStatus, QbIdResolver ids)
    {
        _ids = ids;
        _tables = tables;
        _fields = fields;
        _apps = apps;
        _definitions = definitions;
        _access = access;
        _configuration = configuration;
        _recordActions = recordActions;
        _startImport = startImport;
        _importStatus = importStatus;
    }

    private async Task<AppTable> TableAsync(string dbid, CancellationToken ct)
    {
        // dbid: table Guid or numeric table id.
        Guid id;
        try { id = await _ids.TableAsync(dbid, "dbid", ct); }
        catch (QbApiException) { throw QbXmlErrors.UnknownDbid(); }
        try
        {
            var table = await _tables.GetByPublicIdAsync(id, ct);
            await _access.RequireMembershipByTablePublicIdAsync(id, ct);
            return table;
        }
        catch (PowerBase.Domain.Exceptions.NotFoundException) { throw QbXmlErrors.UnknownDbid(); }
    }

    // ---------------------------------------------------------------- API_ImportFromCSV

    /// <summary>
    /// Synchronous CSV import: every row is added or updated through the same record path as API_AddRecord / API_EditRecord
    /// (validation, permissions, encryption, PowerFlow triggers). Stops at the first failing row and reports it.
    /// </summary>
    public async Task<QbXmlResponse> ImportFromCsvAsync(QbXmlRequest req, CancellationToken ct)
    {
        var table = await TableAsync(req.DbId, ct);
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var csv = req.Get("records_csv");
        // Quickbase: no records_csv is an import of nothing (errcode 0, 0 records).
        if (string.IsNullOrWhiteSpace(csv)) return new QbXmlResponse(req.Action).Add("num_recs_input", 0).Add("num_recs_added", 0);
        var rows = QbCsv.Parse(csv);
        var skipFirst = req.Flag("skipfirst");

        List<int> columns;
        var clist = req.Get("clist");
        if (!string.IsNullOrWhiteSpace(clist))
        {
            columns = clist.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var f) ? f : 0) // Quickbase skips a clist entry that is not a field id
                .ToList();
            var dup = columns.Where(c => c != 0).GroupBy(c => c).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new QbXmlException(52, "Duplicate field", $"Field {dup.Key} appears twice in clist");
        }
        else
        {
            // No clist: the first row names the columns (field labels), as in a Quickbase import with headers.
            if (rows.Count == 0) throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "records_csv is empty");
            columns = rows[0].Select(h =>
            {
                var wanted = QbXmlRecordActions.NormalizeName(h);
                return fields.FirstOrDefault(f => QbXmlRecordActions.NormalizeName(f.Label ?? f.Name) == wanted)?.Fid
                    ?? throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field", $"Column '{h}' does not match a field");
            }).ToList();
            skipFirst = true;
        }
        foreach (var fid in columns.Where(c => c != 0))
            if (fields.All(f => f.Fid != fid)) throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field", $"Field {fid} does not exist");

        var mergeFid = int.TryParse(req.Get("mergeFieldId"), out var m) ? m : (int?)null;
        if (mergeFid is { } mf && !columns.Contains(mf))
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "mergeFieldId must be one of the clist fields");
        // As in upsert: the merge field must be Record ID#, the key field or a unique field.
        if (mergeFid is { } mu && mu != QbIds.RECORD_ID_FID && mu != QbRecordService.KeyFid(table, fields)
            && fields.FirstOrDefault(f => f.Fid == mu) is { IsUnique: false })
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"mergeFieldId {mu} must be a unique field");
        var keyFid = mergeFid ?? QbRecordService.KeyFid(table, fields);

        var data = skipFirst ? rows.Skip(1).ToList() : rows;
        // decimalPercent=1: percent cells are fractions (0.5 = 50%); by default they are whole percents (50 = 50%), as we store them.
        var percentCols = req.Flag("decimalPercent")
            ? columns.Select((fid, i) => (fid, i)).Where(c => fields.FirstOrDefault(f => f.Fid == c.fid) is { } pf
                && QbFieldPropertiesBuilder.TypeOf(pf).FieldType == "percent").Select(c => c.i).ToHashSet()
            : new HashSet<int>();
        var added = 0;
        var updated = 0;
        var matched = 0;
        var rids = new XElement("rids");
        for (var i = 0; i < data.Count; i++)
        {
            var fieldsForRow = new List<QbXmlField>();
            for (var c = 0; c < columns.Count && c < data[i].Count; c++)
                if (columns[c] != 0)
                {
                    var cell = data[i][c];
                    if (percentCols.Contains(c) && decimal.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out var fraction))
                        cell = (fraction * 100m).ToString(CultureInfo.InvariantCulture);
                    fieldsForRow.Add(new QbXmlField(columns[c], null, null, cell));
                }
            var keyCell = fieldsForRow.FirstOrDefault(f => f.Fid == keyFid);
            try
            {
                var (rid, updateId, isNew, changed) = await _recordActions.ImportRowAsync(table, fields, keyFid, keyCell?.Value,
                    fieldsForRow, req.Flag("msInUTC"), ct);
                if (isNew) added++; else { matched++; if (changed) updated++; } // an unchanged row is matched but not "updated"
                rids.Add(new XElement("rid", new XAttribute("update_id", updateId), rid));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var err = QbXmlErrors.From(ex);
                throw new QbXmlException(err.Code, err.Message, $"Row {i + 1 + (skipFirst ? 1 : 0)}: {err.Detail}".TrimEnd(' ', ':'));
            }
        }
        return new QbXmlResponse(req.Action)
            .Add("num_recs_input", data.Count)
            .Add("num_recs_added", added)
            .AddIf(matched > 0, "num_recs_updated", updated) // Quickbase lists it only when the CSV matched existing records
            .Add(rids);
    }

    // ---------------------------------------------------------------- API_RunImport

    public async Task<QbXmlResponse> RunImportAsync(QbXmlRequest req, CancellationToken ct)
    {
        var table = await TableAsync(req.DbId, ct);
        // id: saved import Guid or numeric import id.
        Guid importId;
        try { importId = await _ids.ImportAsync(req.Get("id"), table.Id, "id", ct); }
        catch (QbApiException e) { throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", e.Description); }
        var definition = await _definitions.GetByPublicIdAsync(importId, ct);
        if (definition is null || definition.DestinationTableId != table.Id)
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"Import {importId} does not belong to table {req.DbId}");

        var appId = await _apps.GetPublicIdByIdAsync(table.AppId, ct);
        // Extension (not in Quickbase): a saved import that reads a file needs the file for every run. Pass the id from
        // POST /tables/{tableId}/import-files as <fileid>. Imports from a table need nothing extra, as in Quickbase.
        Guid? fileId = null;
        if (!string.IsNullOrWhiteSpace(req.Get("fileid")))
        {
            if (!Guid.TryParse(req.Get("fileid"), out var f))
                throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "Invalid 'fileid'");
            fileId = f;
        }
        var started = await _startImport.StartAsync(importId, ImportTrigger.Api, null, _configuration["Frontend:BaseUrl"], appId, null, fileId, ct);

        var deadline = DateTime.UtcNow + RUN_IMPORT_TIMEOUT;
        ImportRunStatusResponse status;
        while (true)
        {
            status = await _importStatus.HandleAsync(appId, started.RunId, ct);
            if (status.Status is not (ImportRunStatus.Queued or ImportRunStatus.Running) || DateTime.UtcNow >= deadline) break;
            await Task.Delay(POLL_INTERVAL, ct);
        }

        if (status.Status is ImportRunStatus.Queued or ImportRunStatus.Running)
            return new QbXmlResponse(req.Action).Add("import_status",
                $"The import is still running (run {started.RunId}); {status.Inserted} new records were created so far.");
        if (status.Status is ImportRunStatus.Failed or ImportRunStatus.Cancelled)
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", status.ErrorDetail ?? $"The import {status.Status}.");
        return new QbXmlResponse(req.Action).Add("import_status", ImportStatusText(status));
    }

    /// <summary>Quickbase-style summary text, e.g. "3 new records were created."</summary>
    public static string ImportStatusText(ImportRunStatusResponse s)
    {
        var parts = new List<string> { $"{s.Inserted} new record{(s.Inserted == 1 ? " was" : "s were")} created." };
        if (s.Updated > 0) parts.Add($"{s.Updated} record{(s.Updated == 1 ? " was" : "s were")} updated.");
        if (s.Unchanged > 0) parts.Add($"{s.Unchanged} record{(s.Unchanged == 1 ? " was" : "s were")} unchanged.");
        if (s.Skipped > 0) parts.Add($"{s.Skipped} row{(s.Skipped == 1 ? " was" : "s were")} skipped.");
        if (s.Errored > 0) parts.Add($"{s.Errored} row{(s.Errored == 1 ? " has" : "s have")} errors.");
        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- API_GenAddRecordForm

    /// <summary>
    /// Quickbase returns an HTML add-record page with the given values filled in. We return an HTML page that opens our own
    /// add-record form (user decision: Angular form) with the values passed as <c>?prefill_{fid}=</c>, which that form reads.
    /// </summary>
    public async Task<QbXmlResponse> GenAddRecordFormAsync(QbXmlRequest req, CancellationToken ct)
    {
        var table = await TableAsync(req.DbId, ct);
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var appId = await _apps.GetPublicIdByIdAsync(table.AppId, ct);
        var query = new List<string>();
        foreach (var xf in req.Fields)
        {
            var field = xf.Fid is { } fid ? fields.FirstOrDefault(f => f.Fid == fid)
                : fields.FirstOrDefault(f => QbXmlRecordActions.NormalizeName(f.Label ?? f.Name) == QbXmlRecordActions.NormalizeName(xf.Name));
            if (field?.Fid is null) throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field", $"Field '{(object?)xf.Fid ?? xf.Name}' does not exist");
            query.Add($"prefill_{field.Fid}={Uri.EscapeDataString(xf.Value)}");
        }
        var baseUrl = (_configuration["Frontend:BaseUrl"] ?? "").TrimEnd('/');
        var url = $"{baseUrl}/app/{appId}/tables/{table.PublicId}/records/new" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return new QbXmlResponse(req.Action) { Html = RedirectPage(url) };
    }

    public static string RedirectPage(string url)
    {
        var safe = WebUtility.HtmlEncode(url);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Add Record</title>");
        sb.Append("<meta http-equiv=\"refresh\" content=\"0;url=").Append(safe).Append("\"></head>");
        sb.Append("<body><p><a href=\"").Append(safe).Append("\">Open the add-record form</a></p></body></html>");
        return sb.ToString();
    }
}
