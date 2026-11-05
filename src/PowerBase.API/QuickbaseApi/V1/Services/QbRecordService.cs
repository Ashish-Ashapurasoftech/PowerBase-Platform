using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// Quickbase upsert / deleteRecords on top of our existing record commands (called, never modified), so validation,
/// role permissions, encryption, formulas, audit and PowerFlow triggers behave exactly as for the app's own writes.
/// </summary>
public sealed class QbRecordService
{
    /// <summary>Records deleted per BulkDeleteRecordsCommand call (its own limit is 500).</summary>
    public const int DELETE_CHUNK = 500;

    private readonly IAppTableRepository _tables;
    private readonly IAppRepository _apps;
    private readonly IAppFieldRepository _fields;
    private readonly IRecordRepository _records;
    private readonly IUserRepository _users;
    private readonly IAppAccessService _access;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly CreateRecordCommandHandler _create;
    private readonly UpdateRecordCommandHandler _update;
    private readonly GetRecordQueryHandler _get;
    private readonly BulkDeleteRecordsCommandHandler _bulkDelete;
    private readonly QbUserValueResolver _userValues;
    private readonly IFileStorageService _storage;
    private readonly QbIdResolver _ids;
    private readonly QbReferenceTypes _referenceTypes;
    private readonly IQueryContext _identity;

    public QbRecordService(IAppRepository apps, IAppTableRepository tables, IAppFieldRepository fields, IRecordRepository records, IUserRepository users,
        IAppAccessService access, IRolePermissionEnforcer enforcer, CreateRecordCommandHandler create, UpdateRecordCommandHandler update,
        GetRecordQueryHandler get, BulkDeleteRecordsCommandHandler bulkDelete, QbUserValueResolver userValues,
        IFileStorageService storage, IQueryContext identity, QbIdResolver ids, QbReferenceTypes referenceTypes)
    {
        _referenceTypes = referenceTypes;
        _ids = ids;
        _storage = storage;
        _identity = identity;
        _apps = apps;
        _userValues = userValues;
        _tables = tables;
        _fields = fields;
        _records = records;
        _users = users;
        _access = access;
        _enforcer = enforcer;
        _create = create;
        _update = update;
        _get = get;
        _bulkDelete = bulkDelete;
    }

    public sealed record UpsertOutcome(QbUpsertResponse Response, bool HasLineErrors);

    public async Task<UpsertOutcome> UpsertAsync(QbUpsertRequest request, CancellationToken ct)
    {
        if (request.Data is null) throw QbApiException.BadRequest("Required key 'data' not found");
        if (request.Data.Count == 0) throw QbApiException.BadRequest("'data' must NOT have fewer than 1 items");
        for (var r = 0; r < request.Data.Count; r++)
            foreach (var (key, cell) in request.Data[r])
                if (cell.ValueKind != JsonValueKind.Object || !cell.TryGetProperty("value", out _))
                    throw QbApiException.BadRequest($"'Value' property missing for field id {key} row #{r + 1}");
        if (string.IsNullOrWhiteSpace(request.To)) throw QbApiException.BadRequest("Required key 'to' not found");
        var tableId = await TableOrInvalidRequestAsync(request.To, "to", ct);
        await _access.RequireMembershipByTablePublicIdAsync(tableId, ct);
        var table = await _tables.GetByPublicIdAsync(tableId, ct);
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var mergeFid = request.MergeFieldId is { } m && m.ValueKind != JsonValueKind.Null
            ? QbIds.ParseFid(m.ValueKind == JsonValueKind.Number ? m.GetRawText() : m.GetString(), "mergeFieldId")
            : (int?)null;
        if (mergeFid is { } mf && !byFid.ContainsKey(mf))
            throw QbApiException.InvalidInput("Invalid merge field.");
        // Quickbase: the merge field must be the key field, Record ID# or a field that requires unique values.
        if (mergeFid is { } mu && mu != QbIds.RECORD_ID_FID && mu != KeyFid(table, fields) && !byFid[mu].IsUnique)
            throw QbApiException.InvalidInput("Merge field must be unique");
        var keyFid = mergeFid ?? KeyFid(table, fields);

        var returnFids = (request.FieldsToReturn ?? new()).Select(e =>
            QbIds.ParseFid(e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString(), "fieldsToReturn")).Distinct().ToList();
        if (returnFids.Count > 0 && !returnFids.Contains(QbIds.RECORD_ID_FID)) returnFids.Insert(0, QbIds.RECORD_ID_FID);

        var created = new List<long>();
        var updated = new List<long>();
        var unchanged = new List<long>();
        var lineErrors = new Dictionary<string, IReadOnlyList<string>>();
        var processed = new List<(long Rid, Guid PublicId)>();

        for (var i = 0; i < request.Data.Count; i++)
        {
            var line = (i + 1).ToString();
            try
            {
                var values = await ParseRowAsync(request.Data[i], byFid, ct);
                // A row that carries Record ID# (fid 3) and no merge field updates that record, as in Quickbase.
                var rowKey = mergeFid is null && values.ContainsKey(QbIds.RECORD_ID_FID) ? QbIds.RECORD_ID_FID : keyFid;
                var existing = await FindExistingAsync(table, fields, rowKey, values, ct);
                values.Remove(QbIds.RECORD_ID_FID);
                if (existing is null)
                {
                    var result = await _create.HandleAsync(new CreateRecordCommand(tableId, values), ct);
                    var rid = await _records.GetRecordIdByPublicIdAsync(table, result.Id, ct: ct);
                    NoteCreated(table, rid, values);
                    created.Add(rid);
                    processed.Add((rid, result.Id));
                }
                else
                {
                    var (rid, publicId) = existing.Value;
                    if (await IsUnchangedAsync(table, rid, tableId, publicId, values, byFid, ct))
                        unchanged.Add(rid);
                    else
                    {
                        await _update.HandleAsync(new UpdateRecordCommand(tableId, publicId, values), ct);
                        updated.Add(rid);
                    }
                    processed.Add((rid, publicId));
                }
            }
            catch (Exception ex) when (ex is DomainException or QbApiException or UnauthorizedAccessException or InvalidOperationException or FormatException
                                           or ArgumentException or JsonException or InvalidCastException or OverflowException
                                           or System.Data.Common.DbException)
            {
                lineErrors[line] = new[] { LineMessage(ex) };
            }
        }

        var data = new List<IDictionary<string, QbCell>>();
        if (returnFids.Count > 0)
        {
            var userFields = returnFids.Where(f => byFid.TryGetValue(f, out var x) && QbUserValueResolver.IsStoredUserField(x))
                .Select(f => byFid[f]).ToList();
            var users = await _userValues.ResolveAsync(table, userFields, processed.Select(p => p.PublicId).Distinct().ToList(), ct);
            var fileBase = $"/apps/{await _apps.GetPublicIdByIdAsync(table.AppId, ct)}/files/download";
            var refTypes = await _referenceTypes.ForAsync(fields, ct);
            foreach (var (rid, publicId) in processed)
                data.Add(await ReadFieldsAsync(tableId, rid, publicId, returnFids, byFid, users.GetValueOrDefault(publicId), fileBase, refTypes, ct));
        }

        return new UpsertOutcome(new QbUpsertResponse
        {
            Data = data,
            Metadata = new QbUpsertMetadata
            {
                CreatedRecordIds = created,
                LineErrors = lineErrors.Count > 0 ? lineErrors : null,
                UnchangedRecordIds = unchanged,
                UpdatedRecordIds = updated,
                TotalNumberOfRecordsProcessed = created.Count + updated.Count + unchanged.Count,
            },
        }, lineErrors.Count > 0);
    }

    /// <summary>A table id Quickbase cannot use → its "Invalid request" 400 with its wording.</summary>
    private async Task<Guid> TableOrInvalidRequestAsync(string? raw, string param, CancellationToken ct)
    {
        try { return await _ids.TableAsync(raw, param, ct); }
        catch (QbApiException e) when (e.StatusCode is 400 or 404)
        {
            throw QbApiException.InvalidRequest(APP_GONE);
        }
    }

    /// <summary>Quickbase's text for an app / table id it cannot find.</summary>
    public const string APP_GONE = "The application does not exist or was deleted. If you followed a link to get here, you may want to inform the author of that link that the application no longer exists.";

    private static string LineMessage(Exception ex) => ex switch
    {
        QbApiException q => string.IsNullOrEmpty(q.Description) ? q.Message : q.Description,
        _ => ex.Message,
    };

    public static int KeyFid(AppTable table, IReadOnlyList<AppField> fields) =>
        table.KeyFieldId is { } keyId && fields.FirstOrDefault(f => f.Id == keyId)?.Fid is { } fid ? fid : QbIds.RECORD_ID_FID;

    /// <summary>Converts one Quickbase row <c>{"6": {"value": ...}}</c> into our fid → value map.</summary>
    private async Task<Dictionary<long, object?>> ParseRowAsync(Dictionary<string, JsonElement> row, IReadOnlyDictionary<long, AppField> byFid, CancellationToken ct)
    {
        var values = new Dictionary<long, object?>();
        foreach (var (key, cell) in row)
        {
            if (!long.TryParse(key, out var fid) || !byFid.TryGetValue(fid, out var field))
                throw QbApiException.BadRequest($"Unknown field with Id \"{key}\".");
            var v = cell.GetProperty("value");
            if (v.ValueKind == JsonValueKind.Null && fid != QbIds.RECORD_ID_FID) continue; // Quickbase: "value": null leaves the field as it is
            if (fid != QbIds.RECORD_ID_FID && ReadOnlyKind(field) is { } kind)
                throw QbApiException.BadRequest($"Can not change the value of the {kind} field with ID \"{fid}\".");
            values[fid] = CheckValue(field, await ToOurValueAsync(v, field, ct));
        }
        return values;
    }

    /// <summary>"built-in" / "formula" / "summary" / "lookup" for a field Quickbase does not let you write; else null.</summary>
    public static string? ReadOnlyKind(AppField field)
    {
        if (field.Fid is >= 1 and <= 5) return "built-in";
        return QbFieldPropertiesBuilder.TypeOf(field).Mode switch
        {
            "formula" => "formula", "summary" => "summary", "lookup" => "lookup", "virtual" => "formula", _ => null,
        };
    }

    private async Task<object?> ToOurValueAsync(JsonElement v, AppField field, CancellationToken ct)
    {
        var isUser = field.TypeCode is "User" or "MultiUser";
        if (isUser)
        {
            var items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : new List<JsonElement> { v };
            var ids = new List<string>();
            foreach (var item in items)
            {
                // Quickbase stores an e-mail / id it cannot find as "no user" instead of rejecting the row.
                try { if (await ResolveUserAsync(item, ct) is { } id) ids.Add(id); }
                catch (QbApiException) { }
                catch (NotFoundException) { }
            }
            if (ids.Count == 0) return null;
            return field.TypeCode == "MultiUser" ? JsonSerializer.Serialize(ids.Distinct().ToList()) : ids[0];
        }
        if (field.TypeCode is "NumericRange" or "DateRange")
            return RangeValue(v, field);
        var qbType = QbFieldPropertiesBuilder.TypeOf(field).FieldType;
        if (qbType == "address" && v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array)
            throw QbApiException.BadRequest($"Incompatible value for field with ID \"{field.Fid}\".");
        if (qbType == "duration" && v.ValueKind == JsonValueKind.Number)
            return v.GetDecimal() / QbValueFormatter.MS_PER_MINUTE; // Quickbase milliseconds → our minutes
        if (qbType == "percent" && v.ValueKind == JsonValueKind.Number)
            return v.GetDecimal() * 100m;                           // Quickbase 0.75 → our 75 (%)
        if (qbType == "date" && v.ValueKind == JsonValueKind.String)
        {
            // REST dates: YYYY-MM-DD or a keyword ("today", "N days ago"); MM-DD-YYYY is rejected, as in Quickbase.
            var d = QbInputValues.Date(v.GetString());
            if (string.IsNullOrWhiteSpace(d)) return null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$")
                || !DateTime.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                throw Incompatible(field);
            return d;
        }
        if (qbType == "timestamp" && v.ValueKind == JsonValueKind.String)
        {
            // REST date-times need a time part (ISO 8601) or "now" / "today".
            var raw = v.GetString()!.Trim();
            if (raw.Length == 0) return null;
            var keyword = raw.Equals("now", StringComparison.OrdinalIgnoreCase) || raw.Equals("today", StringComparison.OrdinalIgnoreCase);
            var dt = QbInputValues.DateTime(raw);
            if (!keyword && (!raw.Contains('T') || dt is null || !System.Text.RegularExpressions.Regex.IsMatch(dt, @"^\d{4}-\d{2}-\d{2}T")))
                throw Incompatible(field);
            return dt;
        }
        if (qbType is "text" or "rich-text" or "text-multi-line" or "text-multiple-choice" or "email" or "phone" or "url" && v.ValueKind is JsonValueKind.Number)
            return v.GetRawText();                                   // Quickbase stores 123 in a text field as "123"
        if (qbType == "multitext" && v.ValueKind == JsonValueKind.String)
            return string.IsNullOrEmpty(v.GetString()) ? null : JsonSerializer.Serialize(new[] { v.GetString() });
        if (qbType == "checkbox" && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return false;                                            // Quickbase stores false for anything it cannot read
        if (qbType == "file" && v.ValueKind == JsonValueKind.Object)
            return await SaveFileValueAsync(v, field.Fid ?? 0, ct);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => JsonSerializer.Serialize(v),
            JsonValueKind.Null => null,
            // Address accepts {"street1","street2","city","state","zip","country"} — stored as that JSON text.
            JsonValueKind.Object when qbType == "address" => v.GetRawText(),
            JsonValueKind.Object => throw Incompatible(field),
            _ => null,
        };
    }

    private static QbApiException Incompatible(AppField field) => QbApiException.BadRequest($"Incompatible value for field with ID \"{field.Fid}\".");

    /// <summary>
    /// Checks a converted value against its field before it reaches the record command (Quickbase rejects these per row):
    /// numbers must be numbers, a time of day must be a time, a checkbox a boolean, a choice one of the field's choices
    /// (Quickbase fields do not allow new choices by default), a rating within 0..max. Returns the value, normalised.
    /// </summary>
    public static object? CheckValue(AppField field, object? value, bool lenient = false)
    {
        if (value is null || value is string { Length: 0 }) return value;
        var type = QbFieldPropertiesBuilder.TypeOf(field).FieldType;
        var mode = QbFieldPropertiesBuilder.TypeOf(field).Mode;
        if (mode is "formula" or "lookup" or "summary" or "virtual") return value; // read-only: reported by the caller
        object? Bad(string? text = null) => lenient ? null : throw QbApiException.BadRequest(text ?? $"Incompatible value for field with ID \"{field.Fid}\".");
        static decimal? Num(object v) => v switch
        {
            decimal d => d, long l => l, int i => i, double db => (decimal)db,
            string s when decimal.TryParse(s.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        switch (type)
        {
            // Quickbase stores a duration it cannot read as empty; other numbers must be numbers (any rating is kept).
            case "duration" when field.TypeCode is not ("NumericRange" or "Reference"):
                return value is bool || Num(value) is not { } dn ? null : (value is string ? dn : value);
            case "numeric" or "currency" or "percent" or "rating" when field.TypeCode is not ("NumericRange" or "Reference"):
                if (value is bool || Num(value) is not { } n) return Bad();
                return value is string ? n : value;
            case "timeofday":
            {
                var text = value.ToString()!.Trim();
                if (!TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
                    return Bad($"The value for Time Of Day field with ID \"{field.Fid}\" is invalid.");
                return t.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
            case "checkbox":
                // Quickbase: "yes"/"true"/"1" are true, anything else it cannot read is false (no error).
                return value switch
                {
                    bool => value,
                    string s => s.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on" or "y" or "checked",
                    decimal d => d != 0,
                    long l => l != 0,
                    _ => false,
                };
            case "text" or "rich-text" or "text-multi-line" or "text-multiple-choice" or "email" or "phone" or "url" when value is string str:
            {
                var max = MaxLength(field);
                if (max > 0 && str.Length > max)
                    return Bad($"Value \"{str}\" exceeds the length limit for field with ID \"{field.Fid}\".");
                return value;
            }
            default:
                // Choices: Quickbase adds a value that is not in the list (new choices are allowed), so it is kept as given.
                return value;
        }
    }

    private static int MaxLength(AppField field)
    {
        if (field.MaxLength is int m && m > 0) return m;
        if (string.IsNullOrWhiteSpace(field.Settings)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(field.Settings);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("MaxLength", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var a)) return a;
                if (p.Name.Equals("Validation", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var q in p.Value.EnumerateObject())
                        if (q.Name.Equals("MaxLength", StringComparison.OrdinalIgnoreCase) && q.Value.ValueKind == JsonValueKind.Number && q.Value.TryGetInt32(out var b)) return b;
            }
        }
        catch (JsonException) { }
        return 0;
    }

    private static List<string>? ChoiceOptions(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(field.Settings);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("ChoiceSource", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object) return null;
            }
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Options", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    return list.Count == 0 ? null : list;
                }
        }
        catch (JsonException) { }
        return null;
    }

    private static int RatingMax(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return 5;
        try
        {
            using var doc = JsonDocument.Parse(field.Settings);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Max", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var m) && m > 0) return m;
        }
        catch (JsonException) { }
        return 5;
    }

    /// <summary>
    /// Range fields (no Quickbase equivalent): <c>{"start": a, "end": b}</c> or the text "a - b" / "a to b".
    /// Anything else is a line error instead of reaching the record handler.
    /// </summary>
    public static object? RangeValue(JsonElement v, AppField field)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;
        string? start, end;
        if (v.ValueKind == JsonValueKind.Object)
        {
            start = Part(v, "start"); end = Part(v, "end");
        }
        else if (v.ValueKind == JsonValueKind.String)
        {
            var parts = System.Text.RegularExpressions.Regex.Split(v.GetString()!.Trim(), @"\s+(?:-|to)\s+|\.\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (parts.Length != 2) throw QbApiException.BadRequest($"Field {field.Fid} needs a range: {{\"start\":…,\"end\":…}} or \"start - end\"");
            start = parts[0].Trim(); end = parts[1].Trim();
        }
        else throw QbApiException.BadRequest($"Field {field.Fid} needs a range: {{\"start\":…,\"end\":…}} or \"start - end\"");

        object? Conv(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (field.TypeCode == "DateRange")
                return QbInputValues.Date(s) ?? throw QbApiException.BadRequest($"Field {field.Fid}: '{s}' is not a date");
            return decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d : throw QbApiException.BadRequest($"Field {field.Fid}: '{s}' is not a number");
        }
        var json = JsonSerializer.Serialize(new { start = Conv(start), end = Conv(end) });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();

        static string? Part(JsonElement o, string name)
        {
            foreach (var p in o.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => p.Value.GetString(), _ => p.Value.GetRawText() };
            return null;
        }
    }

    /// <summary>Quickbase file input:<c>{"fileName": "...", "data": "&lt;base64&gt;"}</c>. Stored like the app's own upload.</summary>
    private async Task<string> SaveFileValueAsync(JsonElement v, int fileFid, CancellationToken ct)
    {
        var name = v.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? Path.GetFileName(n.GetString()) : null;
        var data = v.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || data is null)
            throw QbApiException.BadRequest($"Can not find file attachment content for field with ID '{fileFid}'.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data.Trim()); }
        catch (FormatException) { throw QbApiException.BadRequest($"Can not find file attachment content for field with ID '{fileFid}'."); }
        await using var stream = new MemoryStream(bytes);
        var type = QbInputValues.ContentType(name);
        var stored = await _storage.SaveAsync(stream, name, type, ct);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = stored.Name, ["path"] = stored.Path, ["size"] = stored.Size, ["type"] = stored.ContentType ?? type,
            ["uploadedOn"] = DateTime.UtcNow, ["uploadedBy"] = _identity.UserName,
        });
    }

    /// <summary>A Quickbase user value is an object (id / email) or a string (email or user id). Returns our user PublicId.</summary>
    public async Task<string?> ResolveUserAsync(JsonElement item, CancellationToken ct)
    {
        string? id = null, email = null;
        if (item.ValueKind == JsonValueKind.Number) id = item.GetRawText();
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) id = i.GetString();
            else if (item.TryGetProperty("id", out var ni) && ni.ValueKind == JsonValueKind.Number) id = ni.GetRawText();
            if (item.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String) email = e.GetString();
        }
        else if (item.ValueKind == JsonValueKind.String)
        {
            var s = item.GetString();
            if (s?.Contains('@') == true) email = s; else id = s;
        }
        if (Guid.TryParse(id, out var guid)) return guid.ToString();
        // A numeric user id (our internal id) of someone in the caller's account.
        if (QbIdResolver.Classify(id, out _, out _) == QbIdResolver.IdKind.Number)
            return (await _ids.UserAsync(id, "user id", ct)).ToString();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var user = await _users.GetByEmailAsync(email.Trim(), ct)
                ?? throw QbApiException.BadRequest($"User '{email}' was not found");
            return user.PublicId.ToString();
        }
        if (!string.IsNullOrWhiteSpace(id)) throw QbApiException.BadRequest($"User id '{id}' is not valid");
        return null;
    }

    private readonly Dictionary<long, bool> _appEncrypted = new();
    private readonly Dictionary<(long Table, int Fid), Dictionary<string, List<long>>> _encryptedIndexes = new();

    private async Task<bool> IsEncryptedAsync(AppTable table, AppField field, CancellationToken ct)
    {
        if (!_appEncrypted.TryGetValue(table.AppId, out var appEncrypted))
            _appEncrypted[table.AppId] = appEncrypted = (await _apps.GetByIdAsync(table.AppId, ct)).IsEncrypted;
        return Application.Relationships.EncryptedRowFilter.IsEncrypted(field, appEncrypted);
    }

    private static string NormalizeKey(string value) => value.Trim().ToLowerInvariant();

    /// <summary>Decrypted key value → record ids for one encrypted field, built once per request and kept current as rows are added.</summary>
    private async Task<Dictionary<string, List<long>>> EncryptedIndexAsync(AppTable table, IReadOnlyList<AppField> fields, AppField keyField, CancellationToken ct)
    {
        var cacheKey = (table.Id, keyField.Fid!.Value);
        if (_encryptedIndexes.TryGetValue(cacheKey, out var cached)) return cached;
        var column = Domain.Constants.PhysicalNaming.GetPhysicalColumnName(keyField);
        var index = new Dictionary<string, List<long>>();
        const int pageSize = 5000;
        for (var page = 1; ; page++)
        {
            var rows = await _records.ListAsync(table, fields, page, pageSize, filterTree: null, ct: ct);
            foreach (var row in rows)
            {
                if (!row.TryGetValue(column, out var v) || v is null || !row.TryGetValue("Id", out var id) || id is null) continue;
                var key = NormalizeKey(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<long>();
                list.Add(Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (rows.Count < pageSize) break;
        }
        return _encryptedIndexes[cacheKey] = index;
    }

    /// <summary>Keeps a cached encrypted-key index current after this request adds a record.</summary>
    private void NoteCreated(AppTable table, long rid, IReadOnlyDictionary<long, object?> values)
    {
        foreach (var ((tableId, fid), index) in _encryptedIndexes)
        {
            if (tableId != table.Id || !values.TryGetValue(fid, out var v) || v is null) continue;
            var key = NormalizeKey(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            if (key.Length == 0) continue;
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<long>();
            list.Add(rid);
        }
    }

    /// <summary>Finds the record a row should update: by Record ID# (fid 3) or by the merge/key field value.</summary>
    public async Task<(long Rid, Guid PublicId)?> FindExistingAsync(AppTable table, IReadOnlyList<AppField> fields, int keyFid,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct)
    {
        if (!values.TryGetValue(keyFid, out var keyValue) || keyValue is null || (keyValue is string ks && ks.Length == 0))
            return null;

        long[] ids;
        if (keyFid == QbIds.RECORD_ID_FID)
        {
            if (!long.TryParse(Convert.ToString(keyValue, System.Globalization.CultureInfo.InvariantCulture), out var rid))
                throw QbApiException.BadRequest($"Can not find record by ID \"{keyValue}\".");
            if (!await _records.ExistsAsync(table, rid, ct))
                throw QbApiException.BadRequest($"Can not find record by ID \"{rid}\".");
            ids = new[] { rid };
        }
        else
        {
            var keyText = Convert.ToString(keyValue is JsonElement je ? je.ToString() : keyValue, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var keyField = fields.FirstOrDefault(f => f.Fid == keyFid);
            if (keyField is not null && await IsEncryptedAsync(table, keyField, ct))
            {
                // Encrypted values are stored as ciphertext, so SQL "=" can never match: compare decrypted values,
                // the same approach the Copy Records PowerFlow step uses for an encrypted merge field.
                var index = await EncryptedIndexAsync(table, fields, keyField, ct);
                ids = index.TryGetValue(NormalizeKey(keyText), out var found) ? found.ToArray() : Array.Empty<long>();
            }
            else
            {
                var filter = new FilterGroup { Logic = "AND", Nodes = { new FilterNode { Condition = new FilterCondition
                {
                    FieldId = keyFid, Operator = "eq", Value = keyText,
                } } } };
                ids = (await _records.ListMatchingRowIdsAsync(table, fields, filter, maxRows: 2, ct: ct)).ToArray();
            }
            if (ids.Length == 0) return null;
            if (ids.Length > 1) throw QbApiException.BadRequest($"More than one record matches field {keyFid} value '{keyValue}'");
        }

        var rows = await _records.GetRowsByIdsAsync(table, Array.Empty<AppField>(), ids, ct);
        if (!rows.TryGetValue(ids[0], out var row) || !row.TryGetValue("PublicId", out var pid) || pid is not Guid publicId)
            throw QbApiException.BadRequest($"Record ID# {ids[0]} does not exist");
        return (ids[0], publicId);
    }

    /// <summary>True when every submitted value already equals the record's current value.</summary>
    private async Task<bool> IsUnchangedAsync(AppTable table, long rid, Guid tableId, Guid publicId, IReadOnlyDictionary<long, object?> values,
        IReadOnlyDictionary<long, AppField> byFid, CancellationToken ct)
    {
        if (values.Count == 0) return true;
        var current = await _get.HandleAsync(new GetRecordQuery(tableId, publicId), ct);
        foreach (var (fid, value) in values)
            if (!await FieldUnchangedAsync(table, rid, byFid.GetValueOrDefault(fid), fid, value, current, ct)) return false;
        return true;
    }

    /// <summary>True when <paramref name="value"/> equals what the record stores for the field (user fields compared by user id).</summary>
    public async Task<bool> FieldUnchangedAsync(AppTable table, long rid, AppField? field, long fid, object? value,
        Application.Records.RecordResult current, CancellationToken ct)
    {
        if (field is not null && QbUserValueResolver.IsStoredUserField(field))
        {
            // The record query returns user names; compare the stored user ids with the ids the caller sent.
            var row = (await _records.GetRowsByIdsAsync(table, new[] { field }, new[] { rid }, ct)).GetValueOrDefault(rid);
            var stored = QbUserValueResolver.ParseIds(row is not null && row.TryGetValue(QbUserValueResolver.Column(field), out var raw) ? raw : null);
            var sent = new List<long>();
            foreach (var g in QbUserValueResolver.ParseGuids(value))
            {
                try { sent.Add((await _users.GetByPublicIdAsync(g, ct)).Id); }
                catch (NotFoundException) { return false; }
            }
            return stored.Order().SequenceEqual(sent.Distinct().Order());
        }
        current.Fields.TryGetValue(fid.ToString(), out var existing);
        return SameValue(existing, value);
    }

    public static bool SameValue(object? a, object? b)
    {
        static string? Norm(object? v) => v switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } je => Canonical(je),
            JsonElement je => je.ValueKind == JsonValueKind.Null ? null : je.ToString(),
            string s when s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('[') => CanonicalText(s),
            string s => s.Length == 0 ? null : s,
            System.Collections.IEnumerable e => CanonicalText(JsonSerializer.Serialize(e, e.GetType())),
            bool x => x ? "true" : "false",
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("o"),
            decimal m => m.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
        if (AsInstant(a) is { } ia && AsInstant(b) is { } ib) return ia == ib;
        var x = Norm(a);
        var y = Norm(b);
        if (x == y) return true;
        if (x is null || y is null) return false;
        if (decimal.TryParse(x, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dx)
            && decimal.TryParse(y, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dy))
            return dx == dy;
        return string.Equals(x, y, StringComparison.Ordinal);
    }

    /// <summary>JSON text in a stable form (no spaces, numbers without trailing zeros, dates as yyyy-MM-dd when midnight),
    /// so the same multi-select / address / range / user list compares equal however it was written. Empty → null.</summary>
    private static string? CanonicalText(string s)
    {
        try { using var doc = JsonDocument.Parse(s); return Canonical(doc.RootElement); }
        catch (JsonException) { return s.Length == 0 ? null : s; }
    }

    private static string? Canonical(JsonElement e)
    {
        string One(JsonElement x) => x.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", x.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => JsonSerializer.Serialize(p.Name.ToLowerInvariant()) + ":" + One(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", x.EnumerateArray().Select(One)) + "]",
            JsonValueKind.Number => x.GetDecimal().ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String when DateTime.TryParseExact(x.GetString(), new[] { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss'Z'" },
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) && d.TimeOfDay == TimeSpan.Zero
                => "\"" + d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "\"",
            JsonValueKind.String => JsonSerializer.Serialize(x.GetString()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "null",
        };
        var text = One(e);
        return text is "[]" or "{}" or "null" ? null : text;
    }

    /// <summary>A date/time value as a UTC instant when one side is a DateTime and the other parses as one.</summary>
    private static DateTime? AsInstant(object? v) => v switch
    {
        DateTime d => DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind).ToUniversalTime(),
        DateTimeOffset o => o.UtcDateTime,
        string s when s.Length >= 10 && char.IsDigit(s[0]) && s.Contains('-') &&
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var p) => p,
        _ => null,
    };

    private async Task<IDictionary<string, QbCell>> ReadFieldsAsync(Guid tableId, long rid, Guid publicId, IReadOnlyList<int> fids,
        IReadOnlyDictionary<long, AppField> byFid, IReadOnlyDictionary<long, object?>? users, string fileBase,
        IReadOnlyDictionary<long, QbFieldType> refTypes, CancellationToken ct)
    {
        var record = await _get.HandleAsync(new GetRecordQuery(tableId, publicId), ct);
        var cells = new Dictionary<string, QbCell>();
        foreach (var fid in fids)
        {
            if (fid == QbIds.RECORD_ID_FID) { cells["3"] = new QbCell(rid); continue; }
            if (!byFid.TryGetValue(fid, out var field)) continue;
            if (!record.Fields.TryGetValue(fid.ToString(), out var raw)) continue; // hidden from this role
            if (raw is null && fid == QbIds.DATE_MODIFIED_FID) raw = record.CreatedOn; // never empty in Quickbase
            if (users is not null && users.TryGetValue(fid, out var user)) { cells[fid.ToString()] = new QbCell(user); continue; }
            cells[fid.ToString()] = new QbCell(QbValueFormatter.Format(raw, QbFieldPropertiesBuilder.TypeOf(field, refTypes).FieldType, fileBase));
        }
        return cells;
    }

    /// <summary>deleteRecords: soft-deletes every record matching <c>where</c> that the caller may delete.</summary>
    public async Task<QbDeleteRecordsResponse> DeleteAsync(QbDeleteRecordsRequest request, CancellationToken ct)
    {
        if (request.Where is null) throw QbApiException.BadRequest("Required key 'where' not found");
        if (request.Where.Length == 0)
            throw new QbApiException(400, "Bad Request", "Multiple errors found")
            {
                Details = new[] { "'where' must NOT have fewer than 1 characters", "'where' must be array", "'where' must match a schema in anyOf" },
            };
        var tableId = await TableOrInvalidRequestAsync(request.From, "from", ct);
        await _access.RequireMembershipByTablePublicIdAsync(tableId, ct);
        var table = await _tables.GetByPublicIdAsync(tableId, ct);
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView || !access.CanDelete) throw QbApiException.AccessDenied("You do not have permission to delete records in this table.");

        var knownFids = fields.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToHashSet();
        // Quickbase answers a query it cannot use with "numberDeleted": 0 rather than an error (measured on a live realm).
        FilterGroup where;
        try { where = QbWhereParser.Parse(request.Where, knownFids); }
        catch (QbApiException) { return new QbDeleteRecordsResponse(0); }
        var filter = new FilterGroup { Logic = "AND" };
        filter.Nodes.Add(new FilterNode { Group = where });
        if (access.ViewFilter is { } vf) filter.Nodes.Add(new FilterNode { Group = vf });
        if (access.ModifyFilter is { } mf) filter.Nodes.Add(new FilterNode { Group = mf });

        // A condition on an encrypted field cannot be judged in SQL (ciphertext): match decrypted rows in memory instead.
        if (!_appEncrypted.TryGetValue(table.AppId, out var appEncrypted))
            _appEncrypted[table.AppId] = appEncrypted = (await _apps.GetByIdAsync(table.AppId, ct)).IsEncrypted;
        List<long>? encryptedMatches = null;
        if (Application.Relationships.EncryptedRowFilter.TouchesEncrypted(filter, fields, appEncrypted))
        {
            encryptedMatches = new List<long>();
            const int pageSize = 5000;
            for (var page = 1; ; page++)
            {
                var rows = await _records.ListAsync(table, fields, page, pageSize, filterTree: null,
                    restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
                encryptedMatches.AddRange(Application.Relationships.EncryptedRowFilter.MatchingIds(rows, fields, filter));
                if (rows.Count < pageSize) break;
            }
        }

        var deleted = 0;
        var seen = new HashSet<long>();
        var offset = 0;
        while (true)
        {
            IReadOnlyList<long> ids;
            if (encryptedMatches is not null)
            {
                ids = encryptedMatches.Skip(offset).Take(DELETE_CHUNK).ToList();
                offset += ids.Count;
            }
            else
                ids = await _records.ListMatchingRowIdsAsync(table, fields, filter, access.RestrictToCreatedBy, maxRows: DELETE_CHUNK, timeoutSeconds: 120, ct: ct);
            var fresh = ids.Where(seen.Add).ToList();
            if (fresh.Count == 0) break;
            var rows = await _records.GetRowsByIdsAsync(table, Array.Empty<AppField>(), fresh, ct);
            var publicIds = rows.Values.Select(r => r.TryGetValue("PublicId", out var p) && p is Guid g ? g : Guid.Empty)
                .Where(g => g != Guid.Empty).ToList();
            if (publicIds.Count == 0) break;
            await _bulkDelete.HandleAsync(new BulkDeleteRecordsCommand(tableId, publicIds, DELETE_CHUNK), ct);
            deleted += publicIds.Count;
            if (ids.Count < DELETE_CHUNK) break;
        }
        return new QbDeleteRecordsResponse(deleted);
    }
}
