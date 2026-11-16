using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// Record-level Quickbase XML calls: API_AddRecord, API_EditRecord, API_DeleteRecord, API_GetFieldProperties, API_UploadFile.
/// All writes go through our existing record commands (called, not changed).
/// </summary>
public sealed class QbXmlRecordActions
{
    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IAppRepository _apps;
    private readonly IRecordRepository _records;
    private readonly IAppAccessService _access;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IFileStorageService _storage;
    private readonly IQueryContext _identity;
    private readonly QbRecordService _qbRecords;
    private readonly CreateRecordCommandHandler _create;
    private readonly UpdateRecordCommandHandler _update;
    private readonly DeleteRecordCommandHandler _delete;
    private readonly GetRecordQueryHandler _get;
    private readonly IHttpContextAccessor _http;
    private readonly QbIdResolver _ids;
    private readonly V1.Services.QbReferenceTypes _referenceTypes;

    public QbXmlRecordActions(IAppTableRepository tables, IAppFieldRepository fields, IAppRepository apps, IRecordRepository records,
        IAppAccessService access, IRolePermissionEnforcer enforcer, IFileStorageService storage, IQueryContext identity, QbRecordService qbRecords,
        CreateRecordCommandHandler create, UpdateRecordCommandHandler update, DeleteRecordCommandHandler delete, GetRecordQueryHandler get, IHttpContextAccessor http, QbIdResolver ids, V1.Services.QbReferenceTypes referenceTypes)
    {
        _referenceTypes = referenceTypes;
        _ids = ids;
        _http = http;
        _tables = tables;
        _fields = fields;
        _apps = apps;
        _records = records;
        _access = access;
        _enforcer = enforcer;
        _storage = storage;
        _identity = identity;
        _qbRecords = qbRecords;
        _create = create;
        _update = update;
        _delete = delete;
        _get = get;
    }

    private sealed record TableContext(AppTable Table, IReadOnlyList<AppField> Fields)
    {
        public Guid PublicId => Table.PublicId;
    }

    private async Task<TableContext> LoadTableAsync(string dbid, CancellationToken ct)
    {
        // dbid: table Guid or numeric table id.
        Guid tableId;
        try { tableId = await _ids.TableAsync(dbid, "dbid", ct); }
        catch (QbApiException) { throw QbXmlErrors.UnknownDbid(); }
        AppTable table;
        try { table = await _tables.GetByPublicIdAsync(tableId, ct); }
        catch (PowerBase.Domain.Exceptions.NotFoundException) { throw QbXmlErrors.UnknownDbid(); }
        await _access.RequireMembershipByTablePublicIdAsync(tableId, ct);
        return new TableContext(table, await _fields.ListByTableAsync(table.Id, ct));
    }

    // ---------------------------------------------------------------- field matching / values

    public static string NormalizeName(string? label) =>
        Regex.Replace((label ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    private static AppField? TryMatchField(IReadOnlyList<AppField> fields, QbXmlField f)
    {
        try { return MatchField(fields, f); }
        catch (QbXmlException) { return null; }
    }

    private static AppField MatchField(IReadOnlyList<AppField> fields, QbXmlField f)
    {
        AppField? match = null;
        if (f.Fid is { } fid) match = fields.FirstOrDefault(x => x.Fid == fid);
        else if (!string.IsNullOrWhiteSpace(f.Name))
        {
            var wanted = NormalizeName(f.Name);
            match = fields.FirstOrDefault(x => string.Equals(x.Label ?? x.Name, f.Name, StringComparison.OrdinalIgnoreCase))
                ?? fields.FirstOrDefault(x => NormalizeName(x.Label ?? x.Name) == wanted)
                ?? fields.FirstOrDefault(x => NormalizeName(x.Name) == wanted);
        }
        return match ?? throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field", $"Field '{(object?)f.Fid ?? f.Name}' does not exist");
    }

    /// <summary>Converts a Quickbase XML text value ("How to specify values for different field types") into our input value.</summary>
    private async Task<object?> ConvertAsync(AppField field, string text, CancellationToken ct, bool msDuration = false)
    {
        var type = QbFieldPropertiesBuilder.TypeOf(field).FieldType;
        if (string.IsNullOrEmpty(text)) return type == "checkbox" ? false : null;
        if (field.TypeCode is "NumericRange" or "DateRange")
        {
            // Range text "a - b" / "a to b" (or JSON {"start","end"}); XML dates may use MM-DD-YYYY.
            var trimmed = text.Trim();
            using var doc = JsonDocument.Parse(trimmed.StartsWith('{') ? trimmed : JsonSerializer.Serialize(trimmed));
            try { return QbRecordService.RangeValue(doc.RootElement, field); }
            catch (QbApiException e) { throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", e.Description); }
        }
        switch (type)
        {
            case "checkbox":
                return text.Trim().ToLowerInvariant() is "1" or "yes" or "true" or "on";
            case "date":
                return ParseDate(text) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text;
            case "timestamp":
                return ParseDate(text) is { } t ? t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : text;
            case "numeric" or "currency" or "percent" or "rating":
                return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : text;
            case "duration":
                // Quickbase XML: a bare number is days, or milliseconds with msAsDurationDefault=1. We store minutes.
                if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var dur)) return text;
                return msDuration ? dur / QbValueFormatter.MS_PER_MINUTE : dur * 1440m;
            case "multitext":
                return JsonSerializer.Serialize(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            case "user" or "multiuser":
            {
                var ids = new List<string>();
                foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(part));
                    string? id;
                    try { id = await _qbRecords.ResolveUserAsync(doc.RootElement, ct); }
                    catch (QbApiException) { id = null; } // unknown user → stored as empty, as Quickbase does
                    if (id is not null) ids.Add(id);
                }
                if (ids.Count == 0) return null;
                return type == "multiuser" ? JsonSerializer.Serialize(ids) : ids[0];
            }
            default:
                return text;
        }
    }

    /// <summary>Quickbase XML dates: milliseconds since epoch (UTC) or MM-DD-YYYY; ISO is accepted too.</summary>
    public static DateTime? ParseDate(string text)
    {
        var s = text.Trim();
        if (long.TryParse(s, out var ms) && s.Length >= 9) return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        if (DateTime.TryParseExact(s, new[] { "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy HH:mm", "MM-dd-yyyy hh:mm tt" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso)) return iso;
        return null;
    }

    private Task<(Dictionary<long, object?> Values, List<(AppField Field, QbXmlField Xml)> Files)> BuildValuesAsync(
        TableContext t, QbXmlRequest req, CancellationToken ct) => BuildValuesAsync(t, req.Fields, req.Flag("ignoreError"), ct, req.Flag("msAsDurationDefault"));

    private async Task<(Dictionary<long, object?> Values, List<(AppField Field, QbXmlField Xml)> Files)> BuildValuesAsync(
        TableContext t, IEnumerable<QbXmlField> xmlFields, bool ignoreBuiltIn, CancellationToken ct, bool msDuration = false)
    {
        var values = new Dictionary<long, object?>();
        var files = new List<(AppField, QbXmlField)>();
        foreach (var xf in xmlFields)
        {
            // Quickbase ignores a <field> whose fid / name is not in the table.
            var field = TryMatchField(t.Fields, xf);
            if (field is null) continue;
            var fid = field.Fid!.Value;
            if (fid is >= 1 and <= 5)
            {
                if (ignoreBuiltIn) continue;
                throw new QbXmlException(QbXmlErrors.CANNOT_CHANGE_FIELD, "You cannot change the value of this field",
                    $"The field named \"{(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label)}\" with field id {fid} cannot be modified");
            }
            if (!string.IsNullOrEmpty(xf.FileName)) { files.Add((field, xf)); continue; }
            var converted = await ConvertAsync(field, xf.Value, ct, msDuration);
            // XML calls keep Quickbase's leniency: a value it cannot read is stored as empty instead of failing the call.
            try { values[fid] = QbRecordService.CheckValue(field, converted, lenient: true); }
            catch (QbApiException e) { throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", e.Description); }
        }
        return (values, files);
    }

    private async Task<(long UpdateId, Application.Records.RecordResult Record)> UpdateIdAsync(Guid tableId, Guid recordId, CancellationToken ct)
    {
        var record = await _get.HandleAsync(new GetRecordQuery(tableId, recordId), ct);
        var stamp = record.ModifiedOn ?? record.CreatedOn;
        return (new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), record);
    }

    private async Task<(long Rid, Guid PublicId)> FindRecordAsync(TableContext t, QbXmlRequest req, CancellationToken ct)
    {
        var rid = req.Get("rid");
        var key = req.Get("key");
        if (!string.IsNullOrWhiteSpace(rid))
        {
            var values = new Dictionary<long, object?> { [QbIds.RECORD_ID_FID] = rid.Trim() };
            try
            {
                return await _qbRecords.FindExistingAsync(t.Table, t.Fields, QbIds.RECORD_ID_FID, values, ct)
                    ?? throw NoRecord(rid);
            }
            catch (QbApiException) { throw NoRecord(rid); }
        }
        if (!string.IsNullOrWhiteSpace(key))
        {
            var keyFid = QbRecordService.KeyFid(t.Table, t.Fields);
            var values = new Dictionary<long, object?> { [keyFid] = key.Trim() };
            try
            {
                return await _qbRecords.FindExistingAsync(t.Table, t.Fields, keyFid, values, ct)
                    ?? throw new QbXmlException(QbXmlErrors.NO_SUCH_RECORD, "No such record", $"No record has key '{key}'");
            }
            catch (QbApiException ex) { throw new QbXmlException(QbXmlErrors.NO_SUCH_RECORD, "No such record", ex.Description); }
        }
        throw new QbXmlException(QbXmlErrors.NO_SUCH_RECORD, "No such record", "Missing \"rid\" parameter.");
    }

    private static QbXmlException NoRecord(string rid) => new(QbXmlErrors.NO_SUCH_RECORD, "No such record",
        // Quickbase reads a non-numeric rid as 0 in this message
        $"Sorry, there is no record in this application table with the record identifier (rid)  of \"{(long.TryParse(rid.Trim(), out var n) ? n : 0)}\".");

    // ---------------------------------------------------------------- actions

    public async Task<QbXmlResponse> AddRecordAsync(QbXmlRequest req, CancellationToken ct)
    {
        var t = await LoadTableAsync(req.DbId, ct);
        var (values, files) = await BuildValuesAsync(t, req, ct);
        ValidateFiles(files); // before the record exists, so a bad file never leaves a half-added record
        var created = await _create.HandleAsync(new CreateRecordCommand(t.PublicId, values), ct);
        if (files.Count > 0) await SaveFilesAsync(t, created.Id, files, ct);
        var rid = await _records.GetRecordIdByPublicIdAsync(t.Table, created.Id, ct: ct);
        var (updateId, _) = await UpdateIdAsync(t.PublicId, created.Id, ct);
        return new QbXmlResponse(req.Action).Add("rid", rid).Add("update_id", updateId);
    }

    public async Task<QbXmlResponse> EditRecordAsync(QbXmlRequest req, CancellationToken ct)
    {
        var t = await LoadTableAsync(req.DbId, ct);
        var (rid, publicId) = await FindRecordAsync(t, req, ct);
        var (currentUpdateId, current) = await UpdateIdAsync(t.PublicId, publicId, ct);
        if (req.Get("update_id") is { Length: > 0 } wanted && wanted.Trim() != currentUpdateId.ToString(CultureInfo.InvariantCulture))
            throw new QbXmlException(QbXmlErrors.UPDATE_CONFLICT, "Update conflict detected", "") { Extra = new[] { new System.Xml.Linq.XElement("rid", rid) } };

        var (values, files) = await BuildValuesAsync(t, req, ct);
        ValidateFiles(files); // a bad file must not leave the other fields half-saved
        var changed = 0;
        foreach (var (fid, value) in values)
            if (!await _qbRecords.FieldUnchangedAsync(t.Table, rid, t.Fields.FirstOrDefault(f => f.Fid == fid), fid, value, current, ct)) changed++;
        // Nothing actually changes → no write, so the record's update_id stays the same (as in Quickbase).
        if (changed > 0) await _update.HandleAsync(new UpdateRecordCommand(t.PublicId, publicId, values), ct);
        if (files.Count > 0) { await SaveFilesAsync(t, publicId, files, ct); changed += files.Count; }
        var (updateId, _) = await UpdateIdAsync(t.PublicId, publicId, ct);
        return new QbXmlResponse(req.Action).Add("rid", rid).Add("num_fields_changed", changed).Add("update_id", updateId);
    }

    /// <summary>One API_ImportFromCSV row: updates the record whose key field equals <paramref name="keyValue"/>, otherwise adds one.</summary>
    public async Task<(long Rid, long UpdateId, bool IsNew, bool Changed)> ImportRowAsync(AppTable table, IReadOnlyList<AppField> fields, int keyFid,
        string? keyValue, IReadOnlyList<QbXmlField> rowFields, bool msInUtc, CancellationToken ct)
    {
        _ = msInUtc; // millisecond timestamps are always read as UTC
        var t = new TableContext(table, fields);
        (long Rid, Guid PublicId)? existing = null;
        if (!string.IsNullOrWhiteSpace(keyValue))
        {
            try
            {
                existing = await _qbRecords.FindExistingAsync(table, fields, keyFid, new Dictionary<long, object?> { [keyFid] = keyValue.Trim() }, ct);
            }
            catch (QbApiException ex) { throw new QbXmlException(QbXmlErrors.NO_SUCH_RECORD, "No such record", ex.Description); }
        }
        var writable = rowFields.Where(f => f.Fid is not (>= 1 and <= 5)).ToList();
        var (values, files) = await BuildValuesAsync(t, writable, ignoreBuiltIn: true, ct);
        if (existing is { } e)
        {
            var (_, current) = await UpdateIdAsync(table.PublicId, e.PublicId, ct);
            var changed = false;
            foreach (var (fid, value) in values)
                if (!await _qbRecords.FieldUnchangedAsync(table, e.Rid, fields.FirstOrDefault(f => f.Fid == fid), fid, value, current, ct)) { changed = true; break; }
            if (changed) await _update.HandleAsync(new UpdateRecordCommand(table.PublicId, e.PublicId, values), ct);
            var (uid, _) = await UpdateIdAsync(table.PublicId, e.PublicId, ct);
            return (e.Rid, uid, false, changed);
        }
        if (keyFid == QbIds.RECORD_ID_FID && !string.IsNullOrWhiteSpace(keyValue))
            throw new QbXmlException(QbXmlErrors.NO_SUCH_RECORD, "No such record", $"Record ID# {keyValue} does not exist");
        ValidateFiles(files);
        var created = await _create.HandleAsync(new CreateRecordCommand(table.PublicId, values), ct);
        if (files.Count > 0) await SaveFilesAsync(t, created.Id, files, ct);
        var rid = await _records.GetRecordIdByPublicIdAsync(table, created.Id, ct: ct);
        var (updateId, _) = await UpdateIdAsync(table.PublicId, created.Id, ct);
        return (rid, updateId, true, true);
    }

    public async Task<QbXmlResponse> DeleteRecordAsync(QbXmlRequest req, CancellationToken ct)
    {
        var t = await LoadTableAsync(req.DbId, ct);
        var (rid, publicId) = await FindRecordAsync(t, req, ct);
        await _delete.HandleAsync(new DeleteRecordCommand(t.PublicId, publicId), ct);
        return new QbXmlResponse(req.Action).Add("rid", rid);
    }

    public async Task<QbXmlResponse> GetFieldPropertiesAsync(QbXmlRequest req, CancellationToken ct)
    {
        var t = await LoadTableAsync(req.DbId, ct);
        if (string.IsNullOrWhiteSpace(req.Get("fid")))
            throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field", "The  \"fid\" parameter was not specified.");
        if (!int.TryParse(req.Get("fid"), out var fid)) fid = 0;  // Quickbase reads a non-numeric fid as 0
        var access = await _enforcer.GetTableAccessAsync(t.Table, t.Fields, ct);
        var visible = access.Unrestricted ? t.Fields : access.VisibleFields;
        var field = visible.FirstOrDefault(f => f.Fid == fid && !f.IsHidden)
            ?? throw new QbXmlException(QbXmlErrors.NO_SUCH_FIELD, "No such field",
                $"Sorry, there is no field in this application table with the field identifier (fid)  of \"{fid}\".");
        var types = await _referenceTypes.ForAsync(new[] { field }, ct);
        return new QbXmlResponse(req.Action).Add(QbFieldShape.XmlField(field, QbFieldPropertiesBuilder.TypeOf(field, types),
            await _referenceTypes.ContextAsync(field, t.Fields, ct)));
    }

    /// <summary>The <c>&lt;field&gt;</c> element of API_GetFieldProperties (attributes and children from the official sample).</summary>
    public static XElement FieldElement(AppField field, IReadOnlyDictionary<long, QbFieldType>? types = null)
        => QbFieldShape.XmlField(field, QbFieldPropertiesBuilder.TypeOf(field, types));

    /// <summary>Previous fixed-element layout, kept for reference by older tests.</summary>
    public static XElement LegacyFieldElement(AppField field, IReadOnlyDictionary<long, QbFieldType>? types = null)
    {
        var type = QbFieldPropertiesBuilder.TypeOf(field, types);
        var p = QbFieldPropertiesBuilder.BuildProperties(field);
        static string B(object? v) => v switch { true => "1", false => "0", _ => "" };
        return new XElement("field",
            new XAttribute("id", field.Fid!.Value),
            new XAttribute("field_type", type.FieldType),
            new XAttribute("base_type", type.BaseType),
            new XAttribute("mode", type.Mode),
            new XElement("label", string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label),
            new XElement("nowrap", B(QbFieldPropertiesBuilder.NoWrap(field))),
            new XElement("bold", B(QbFieldPropertiesBuilder.Bold(field))),
            new XElement("required", B(field.IsRequired)),
            new XElement("appears_by_default", ""),
            new XElement("find_enabled", B(field.IsSearchable)),
            new XElement("allow_new_choices", B(p["allowNewChoices"])),
            new XElement("sort_as_given", B(p["sortAsGiven"])),
            new XElement("formula", p["formula"] as string ?? ""),
            new XElement("carrychoices", B(p["carryChoices"])),
            new XElement("foreignkey", B(p["foreignKey"])),
            new XElement("unique", B(field.IsUnique)),
            new XElement("doesdatacopy", ""),
            new XElement("fieldhelp", field.HelpText ?? ""),
            new XElement("appears_as", p["appearsAs"] as string ?? ""),
            new XElement("abbreviate", B(p["abbreviate"])),
            new XElement("use_new_window", B(p["useNewWindow"])),
            new XElement("auto_save", B(p["autoSave"])),
            // Summary fields (documented on the API_GetFieldProperties page): the reference and target field ids.
            type.Mode == "summary" ? new XElement("summaryReferenceFid", p["summaryReferenceFieldId"] ?? "") : null,
            type.Mode == "summary" ? new XElement("summaryTargetFid", p["summaryTargetFieldId"] ?? "") : null);
    }

    public async Task<QbXmlResponse> UploadFileAsync(QbXmlRequest req, CancellationToken ct)
    {
        var t = await LoadTableAsync(req.DbId, ct);
        var (_, publicId) = await FindRecordAsync(t, req, ct);
        // Only file fields matter here; other <field> elements (e.g. an e-mail shown in the official sample) are ignored.
        var (_, files) = await BuildValuesAsync(t, req.Fields.Where(f => !string.IsNullOrEmpty(f.FileName)), true, ct);
        if (files.Count == 0)
            throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", "No fields supplied");
        var stored = await SaveFilesAsync(t, publicId, files, ct);
        var appPublicId = await _apps.GetPublicIdByIdAsync(t.Table.AppId, ct);
        var fileFields = new XElement("file_fields");
        foreach (var (field, file) in stored)
            fileFields.Add(new XElement("field", new XAttribute("id", field.Fid!.Value), file.Name,
                new XElement("url", $"{Origin()}/apps/{appPublicId}/files/download?path={Uri.EscapeDataString(file.Path)}&fileName={Uri.EscapeDataString(file.Name)}")));
        return new QbXmlResponse(req.Action).Add(fileFields);
    }

    /// <summary>Quickbase returns a full download URL here; build it from the request's own scheme and host.</summary>
    private string Origin() => _http.HttpContext?.Request is { } r ? $"{r.Scheme}://{r.Host}" : "";

    /// <summary>Checks every file element (File field, valid base64) without storing anything.</summary>
    private static void ValidateFiles(IReadOnlyList<(AppField Field, QbXmlField Xml)> files)
    {
        foreach (var (field, xml) in files)
        {
            if (!field.TypeCode.Equals("File", StringComparison.OrdinalIgnoreCase))
                throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"Field {field.Fid} is not a file attachment field");
            try { _ = Convert.FromBase64String(xml.Value.Trim()); }
            catch (FormatException) { throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"Field {field.Fid} content is not valid base64"); }
        }
    }

    /// <summary>Stores each base64 file and writes it into its File field, in the same value shape the app's upload uses.</summary>
    private async Task<List<(AppField Field, StoredFile File)>> SaveFilesAsync(TableContext t, Guid recordId,
        IReadOnlyList<(AppField Field, QbXmlField Xml)> files, CancellationToken ct)
    {
        var stored = new List<(AppField, StoredFile)>();
        var values = new Dictionary<long, object?>();
        foreach (var (field, xml) in files)
        {
            if (!field.TypeCode.Equals("File", StringComparison.OrdinalIgnoreCase))
                throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"Field {field.Fid} is not a file attachment field");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(xml.Value.Trim()); }
            catch (FormatException) { throw new QbXmlException(QbXmlErrors.INVALID_INPUT, "Invalid input", $"Field {field.Fid} content is not valid base64"); }
            var name = Path.GetFileName(xml.FileName!);
            await using var stream = new MemoryStream(bytes);
            var file = await _storage.SaveAsync(stream, name, ContentType(name), ct);
            values[field.Fid!.Value] = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = file.Name, ["path"] = file.Path, ["size"] = file.Size, ["type"] = file.ContentType ?? ContentType(name),
                ["uploadedOn"] = DateTime.UtcNow, ["uploadedBy"] = _identity.UserName,
            });
            stored.Add((field, file));
        }
        await _update.HandleAsync(new UpdateRecordCommand(t.PublicId, recordId, values), ct);
        return stored;
    }

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain", ".csv" => "text/csv", ".xml" => "application/xml", ".json" => "application/json",
        ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };
}
