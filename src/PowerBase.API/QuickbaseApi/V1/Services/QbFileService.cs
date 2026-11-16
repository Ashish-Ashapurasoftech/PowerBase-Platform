using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// Quickbase "Download file" / "Delete file" (<c>/v1/files/{tableId}/{recordId}/{fieldId}/{versionNumber}</c>) on top of the
/// record read (field permissions) and the app's own revision delete (<see cref="FileReservationService"/>), called, never
/// modified. Version numbers are the ones the record reads report (older revisions first, the current file last); 0 is the
/// current file.
/// </summary>
public sealed class QbFileService
{
    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IRecordRepository _records;
    private readonly IAppAccessService _access;
    private readonly GetRecordQueryHandler _get;
    private readonly FileReservationService _files;
    private readonly IFileStorageService _storage;
    private readonly QbIdResolver _ids;

    public QbFileService(IAppTableRepository tables, IAppFieldRepository fields, IRecordRepository records, IAppAccessService access,
        GetRecordQueryHandler get, FileReservationService files, IFileStorageService storage, QbIdResolver ids)
    {
        _tables = tables;
        _fields = fields;
        _records = records;
        _access = access;
        _get = get;
        _files = files;
        _storage = storage;
        _ids = ids;
    }

    public sealed record Version(int Number, string Path, string? FileName, JsonElement Entry);

    /// <summary>The stored versions of a File value: revisions (oldest first), then the current file.</summary>
    public static IReadOnlyList<Version> Versions(object? value)
    {
        var text = value as string ?? value?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<Version>();
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return Array.Empty<Version>(); }
        if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault();
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<Version>();
        var entries = new List<JsonElement>();
        if (root.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array) entries.AddRange(revs.EnumerateArray());
        entries.Add(root);
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return entries.Select((e, i) => new Version(
                e.TryGetProperty("revisionNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var rn) ? rn : i + 1,
                S(e, "path") ?? string.Empty, S(e, "name"), e))
            .Where(v => v.Path.Length > 0).ToList();
    }

    /// <summary>The requested version: 0 = the current file.</summary>
    public static Version Pick(IReadOnlyList<Version> versions, int number, long fid)
    {
        if (versions.Count == 0) throw QbApiException.NotFound($"No file attachment in field {fid}.");
        var version = number == 0 ? versions[^1] : versions.FirstOrDefault(v => v.Number == number);
        return version ?? throw QbApiException.NotFound($"File version {number} not found in field {fid}.");
    }

    private async Task<(Guid TableId, AppTable Table, Guid RecordId, object? Value)> LoadAsync(string? tableId, string recordId, string fieldId, CancellationToken ct)
    {
        var tablePublicId = await _ids.TableAsync(tableId, "tableId", ct);
        await _access.RequireMembershipByTablePublicIdAsync(tablePublicId, ct);
        var table = await _tables.GetByPublicIdAsync(tablePublicId, ct);
        if (!long.TryParse(recordId, out var rid) || rid <= 0) throw QbApiException.BadRequest($"Invalid record id '{recordId}'.");
        var fid = QbIds.ParseFid(fieldId, "fieldId");
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var field = fields.FirstOrDefault(f => f.Fid == fid && string.Equals(f.TypeCode, "File", StringComparison.OrdinalIgnoreCase))
            ?? throw QbApiException.BadRequest($"Field {fid} is not a file attachment field.");
        var rows = await _records.GetRowsByIdsAsync(table, Array.Empty<AppField>(), new[] { rid }, ct);
        if (!rows.TryGetValue(rid, out var row) || !row.TryGetValue("PublicId", out var p) || p is not Guid publicId)
            throw QbApiException.NotFound($"Record {rid} not found.");
        // The record read applies the role's view scope and hidden fields: a file the caller cannot see is not there.
        var record = await _get.HandleAsync(new GetRecordQuery(tablePublicId, publicId), ct);
        if (!record.Fields.ContainsKey(fid.ToString())) throw QbApiException.AccessDenied($"You do not have permission to view field {fid}.");
        var stored = await _records.GetByPublicIdAsync(table, fields, publicId, ct: ct);
        stored.TryGetValue(Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field), out var value);
        return (tablePublicId, table, publicId, value);
    }

    public async Task<(string Base64, string FileName)> DownloadAsync(string? tableId, string recordId, string fieldId, int version, CancellationToken ct)
    {
        var (_, _, _, value) = await LoadAsync(tableId, recordId, fieldId, ct);
        var picked = Pick(Versions(value), version, QbIds.ParseFid(fieldId, "fieldId"));
        if (_storage is not IFileStorageReadService readable) throw new InvalidOperationException("The configured file storage cannot read files back.");
        await using var stream = await readable.OpenReadAsync(picked.Path, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return (Convert.ToBase64String(buffer.ToArray()), picked.FileName ?? "file");
    }

    /// <summary>Deletes one version; Quickbase answers with that version's details.</summary>
    public async Task<object?> DeleteAsync(string? tableId, string recordId, string fieldId, int version, CancellationToken ct)
    {
        var (tablePublicId, _, recordPublicId, value) = await LoadAsync(tableId, recordId, fieldId, ct);
        var fid = QbIds.ParseFid(fieldId, "fieldId");
        var versions = Versions(value);
        var picked = Pick(versions, version, fid);
        var details = (QbValueFormatter.ToFile(value!, null) as Dictionary<string, object?>)?["versions"] as List<object>;
        var index = versions.ToList().IndexOf(picked);
        await _files.DeleteRevisionAsync(tablePublicId, recordPublicId, fid, picked.Path, ct);
        return details is not null && index >= 0 && index < details.Count ? details[index] : new { versionNumber = picked.Number, fileName = picked.FileName };
    }
}
