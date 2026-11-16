using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// In Quickbase a reference (related-record) field has the type of the parent table's key field — a text key such as
/// "Customer Code" makes the reference field text, the default Record ID# key makes it numeric. Our Reference fields store
/// the parent record id and show the parent's key value; this resolves the Quickbase type for them.
/// </summary>
public sealed class QbReferenceTypes
{
    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly Dictionary<long, QbFieldType> _byParentTable = new();

    public QbReferenceTypes(IAppTableRepository tables, IAppFieldRepository fields)
    {
        _tables = tables;
        _fields = fields;
    }

    /// <summary>Field Id → Quickbase type, for the Reference fields among <paramref name="fields"/>.</summary>
    public async Task<IReadOnlyDictionary<long, QbFieldType>> ForAsync(IEnumerable<AppField> fields, CancellationToken ct)
    {
        var result = new Dictionary<long, QbFieldType>();
        foreach (var field in fields.Where(f => f.TypeCode == "Reference"))
        {
            var parentId = ParentTableId(field.Settings);
            if (parentId is null) continue;
            if (!_byParentTable.TryGetValue(parentId.Value, out var type))
            {
                type = new QbFieldType("numeric", "", "int64"); // default key: Record ID#
                try
                {
                    var parent = await _tables.GetByIdAsync(parentId.Value, ct);
                    if (parent.KeyFieldId is { } keyId)
                    {
                        var key = await _fields.GetByIdInTableAsync(keyId, parent.Id, ct);
                        if (!(key.IsSystem && key.Fid == QbIds.RECORD_ID_FID)) type = QbFieldPropertiesBuilder.TypeOf(key) with { Mode = "" };
                    }
                }
                catch (PowerBase.Domain.Exceptions.NotFoundException) { }
                _byParentTable[parentId.Value] = type;
            }
            result[field.Id] = type;
        }
        return result;
    }

    /// <summary>
    /// What a field takes from a relationship (Quickbase shows it): a summary's target field and a lookup's source field (their
    /// number format), a reference field's parent table (id, alias tag, key field).
    /// </summary>
    public async Task<QbFieldContext?> ContextAsync(AppField field, IReadOnlyList<AppField> tableFields, CancellationToken ct)
    {
        switch (field.TypeCode)
        {
            case "Summary":
                return new QbFieldContext(Inherit: await SummaryTargetAsync(field, ct));
            case "Lookup":
            {
                var refFid = SettingInt(field.Settings, "ReferenceFid");
                var srcFid = SettingInt(field.Settings, "SourceFid");
                var reference = tableFields.FirstOrDefault(f => f.Fid == refFid);
                if (reference is null || srcFid is null || ParentTableId(reference.Settings) is not { } parentId) return null;
                try { return new QbFieldContext(Inherit: (await _fields.ListByTableAsync(parentId, ct)).FirstOrDefault(f => f.Fid == srcFid)); }
                catch (PowerBase.Domain.Exceptions.NotFoundException) { return null; }
            }
            case "Reference":
            {
                if (ParentTableId(field.Settings) is not { } parentId) return null;
                try
                {
                    var parent = await _tables.GetByIdAsync(parentId, ct);
                    var parentFields = await _fields.ListByTableAsync(parent.Id, ct);
                    return new QbFieldContext(MasterTableId: QbIds.Format(parent.PublicId), MasterTableTag: TableTag(parent),
                        MasterKeyFid: QbRecordService.KeyFid(parent, parentFields));
                }
                catch (PowerBase.Domain.Exceptions.NotFoundException) { return null; }
            }
            default:
                return null;
        }
    }

    /// <summary>Quickbase table alias: "_DBID_" + the table name in upper case with "_" between words.</summary>
    public static string TableTag(AppTable table) => !string.IsNullOrWhiteSpace(table.Alias) ? table.Alias!
        : "_DBID_" + System.Text.RegularExpressions.Regex.Replace(table.Name.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');

    private static int? SettingInt(string? settings, string name)
    {
        if (string.IsNullOrWhiteSpace(settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settings);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v)) return v;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The child field a summary field totals (its number format is the summary's, as in Quickbase); null for a count.</summary>
    public async Task<AppField?> SummaryTargetAsync(AppField summary, CancellationToken ct)
    {
        if (summary.TypeCode != "Summary" || string.IsNullOrWhiteSpace(summary.Settings)) return null;
        long? child = null; int? target = null;
        try
        {
            using var doc = JsonDocument.Parse(summary.Settings);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number) continue; // a count summary has no target (null)
                if (p.Name.Equals("ChildTableId", StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt64(out var c)) child = c;
                if (p.Name.Equals("TargetFid", StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var t)) target = t;
            }
        }
        catch (JsonException) { return null; }
        if (child is null || target is null or 0) return null;
        try { return (await _fields.ListByTableAsync(child.Value, ct)).FirstOrDefault(f => f.Fid == target); }
        catch (PowerBase.Domain.Exceptions.NotFoundException) { return null; }
    }

    private static long? ParentTableId(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settings);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("ParentTableId", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                    return p.Value.GetInt64();
        }
        catch (JsonException) { }
        return null;
    }
}
