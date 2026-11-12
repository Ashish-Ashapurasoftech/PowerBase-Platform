using System.Globalization;
using System.Text.Json;

namespace PowerBase.Application.QbMigration;

/// <summary>Values the transformer needs from PowerBase, loaded for a whole chunk before it runs.</summary>
public interface IQbValueLookups
{
    /// <summary>The PowerBase core.User.Id for a QuickBase user's email, or null when there is none.</summary>
    long? UserId(string email);

    /// <summary>For a Reference whose QuickBase parent key is not Record ID#: the parent record's Id for that key value, or null.</summary>
    long? ReferenceId(int pbFid, string key);
}

/// <summary>Format choices that depend on how PowerBase stores a type. Defaults follow the code as read in Stage 0;
/// each one is confirmed by the POC's 100% field comparison before the pilot.</summary>
public sealed record QbTransformOptions
{
    /// <summary>QuickBase returns 75% as 0.75; PowerBase Percent is stored as a whole number (75).</summary>
    public decimal PercentMultiplier { get; init; } = 100m;

    /// <summary>QuickBase returns durations in milliseconds; PowerBase Duration is an INT of minutes.</summary>
    public decimal DurationDivisor { get; init; } = 60_000m;

    /// <summary>Owner written into Record Owner / Last Modified By when a QuickBase user has no PowerBase user.</summary>
    public long FallbackUserId { get; init; }
}

/// <summary>One record ready for the loader: the QuickBase Record ID#, the system columns and the stored field values (keyed by Fid).</summary>
public sealed record QbTransformedRow(
    long RecordId, DateTime? CreatedOn, DateTime? ModifiedOn, long CreatedBy, long? ModifiedBy,
    IReadOnlyDictionary<long, object?> Values);

/// <summary>Turns QuickBase JSON values into the values PowerBase stores. A value that can't be converted is stored as
/// NULL and reported as an issue for that record and field, so a person can see every one; it never stops the chunk.</summary>
public sealed class QbValueTransformer
{
    private readonly IReadOnlyList<QbFieldMapping> _load;
    private readonly ILookup<int, QbFieldMapping> _addressParts;
    private readonly IQbValueLookups _lookups;
    private readonly QbTransformOptions _options;
    private readonly HashSet<int> _referenceByKey;
    // A missing user is reported once per field and email for this transformer (one chunk), not once per record:
    // on a big app every record names the same few users, which made millions of identical issues.
    private readonly HashSet<(int Fid, string Email)> _reportedUsers = [];

    /// <param name="referenceByKeyFids">PowerBase Fids of Reference fields whose QuickBase value is a parent key, not a Record ID#.</param>
    public QbValueTransformer(IReadOnlyList<QbFieldMapping> mappings, IQbValueLookups lookups, QbTransformOptions options,
        IReadOnlyCollection<int>? referenceByKeyFids = null)
    {
        if (!QbFieldMapper.IsLoadable(mappings))
            throw new InvalidOperationException("The table has blocked fields; fix the mapping before loading it.");
        _load = mappings.Where(m => m.Action == QbFieldAction.Load).ToList();
        _addressParts = mappings.Where(m => m.Action == QbFieldAction.AddressPart).ToLookup(m => m.CompositeFid!.Value);
        _lookups = lookups;
        _options = options;
        _referenceByKey = referenceByKeyFids?.ToHashSet() ?? [];
    }

    public QbTransformedRow Transform(QbRow row, List<QbIssue> issues)
    {
        var id = row.RecordId;
        var values = new Dictionary<long, object?>(_load.Count);
        foreach (var m in _load)
        {
            var fid = m.PbFid!.Value;
            if (m.PbTypeCode == "Address")
            {
                values[fid] = AddressJson(row, fid, m.Qb.Id);
                continue;
            }
            row.Values.TryGetValue(m.Qb.Id, out var raw);
            try
            {
                values[fid] = Convert(raw, m, id, issues);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                values[fid] = null;
                issues.Add(new("warning", "QBM_VALUE_NOT_CONVERTED",
                    $"'{m.Qb.Label}': '{Shorten(raw)}' can't be stored as {m.PbTypeCode}; stored empty.", id, m.Qb.Id));
            }
        }

        return new QbTransformedRow(id,
            Timestamp(row, QbSystemFids.DateCreated),
            Timestamp(row, QbSystemFids.DateModified),
            SystemUser(row, QbSystemFids.RecordOwner, id, issues) ?? _options.FallbackUserId,
            SystemUser(row, QbSystemFids.LastModifiedBy, id, issues),
            values);
    }

    private object? Convert(JsonElement raw, QbFieldMapping m, long recordId, List<QbIssue> issues)
    {
        if (IsBlank(raw)) return null;
        switch (m.PbTypeCode)
        {
            case "Text" or "TextMultiLine" or "RichText" or "SingleSelect" or "Email" or "Phone" or "Url":
                return raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.GetRawText();
            case "MultiSelect":
                var choices = raw.ValueKind == JsonValueKind.Array
                    ? raw.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList()
                    : [raw.GetString()!];
                return JsonSerializer.Serialize(choices);
            case "Number" or "Currency":
                return Decimal(raw);
            case "Percent":
                return Decimal(raw) * _options.PercentMultiplier;
            case "Rating":
                return (int)Math.Round(Decimal(raw), MidpointRounding.AwayFromZero);
            case "Duration":
                return (int)Math.Round(Decimal(raw) / _options.DurationDivisor, MidpointRounding.AwayFromZero);
            case "Date":
                return DateTime.ParseExact(raw.GetString()![..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "DateTime":
                return DateTime.Parse(raw.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            case "Time":
                return TimeSpan.Parse(raw.GetString()!, CultureInfo.InvariantCulture);
            case "Boolean":
                return raw.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new FormatException(),
                };
            case "User":
                return UserOf(raw, m, recordId, issues)?.ToString(CultureInfo.InvariantCulture);
            case "MultiUser":
                var ids = raw.ValueKind == JsonValueKind.Array
                    ? raw.EnumerateArray().Select(u => UserOf(u, m, recordId, issues)).Where(u => u.HasValue).Select(u => u!.Value).ToList()
                    : [];
                return ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
            case "Reference":
                return Reference(raw, m, recordId, issues);
            default:
                throw new InvalidOperationException($"No transform for PowerBase type {m.PbTypeCode}.");
        }
    }

    private long? Reference(JsonElement raw, QbFieldMapping m, long recordId, List<QbIssue> issues)
    {
        var fid = m.PbFid!.Value;
        if (!_referenceByKey.Contains(fid))
            return RecordIdOf(raw);

        var key = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText();
        var parent = _lookups.ReferenceId(fid, key);
        if (parent is null)
            issues.Add(new("warning", "QBM_REFERENCE_NOT_FOUND", $"'{m.Qb.Label}': no parent record with key '{Shorten(raw)}'; stored empty.", recordId, m.Qb.Id));
        return parent;
    }

    // QuickBase sends a Record ID# reference as a numeric field, so 1 can arrive as 1.0. A real fraction is not an id.
    private static long RecordIdOf(JsonElement raw)
    {
        var value = raw.ValueKind == JsonValueKind.Number
            ? raw.GetDecimal()
            : decimal.Parse(raw.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (value != decimal.Truncate(value))
            throw new FormatException($"'{value}' is not a whole record id.");
        return decimal.ToInt64(value);
    }

    private long? UserOf(JsonElement user, QbFieldMapping m, long recordId, List<QbIssue> issues)
    {
        var email = EmailOf(user);
        if (email is null) return null;
        var id = _lookups.UserId(email);
        if (id is null && _reportedUsers.Add((m.Qb.Id, email.ToUpperInvariant())))
            issues.Add(new("warning", "QBM_USER_NOT_MAPPED",
                $"'{m.Qb.Label}': no PowerBase user for {email}; stored empty (first record in this chunk; others not listed).", recordId, m.Qb.Id));
        return id;
    }

    private long? SystemUser(QbRow row, int fid, long recordId, List<QbIssue> issues)
    {
        if (!row.Values.TryGetValue(fid, out var raw) || IsBlank(raw)) return null;
        var email = EmailOf(raw);
        var id = email is null ? null : _lookups.UserId(email);
        if (id is null && _reportedUsers.Add((fid, (email ?? Shorten(raw)).ToUpperInvariant())))
            issues.Add(new("info", "QBM_SYSTEM_USER_NOT_MAPPED",
                $"System field {fid}: no PowerBase user for {email ?? Shorten(raw)}; the migration user is used (first record in this chunk; others not listed).", recordId, fid));
        return id;
    }

    /// <summary>QuickBase returns a user as an object with email, id and name.</summary>
    private static string? EmailOf(JsonElement user) => user.ValueKind switch
    {
        JsonValueKind.Object when user.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String => e.GetString(),
        JsonValueKind.String => user.GetString(),
        _ => null,
    };

    private string? AddressJson(QbRow row, int pbFid, int qbFid)
    {
        var parts = new Dictionary<string, string>();
        foreach (var p in _addressParts[qbFid])
            if (row.Values.TryGetValue(p.Qb.Id, out var v) && !IsBlank(v))
                parts[p.AddressKey!] = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        return parts.Count == 0 ? null : JsonSerializer.Serialize(parts);
    }

    private static DateTime? Timestamp(QbRow row, int fid) =>
        row.Values.TryGetValue(fid, out var raw) && raw.ValueKind == JsonValueKind.String
            ? DateTime.Parse(raw.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;

    private static decimal Decimal(JsonElement raw) => raw.ValueKind == JsonValueKind.Number
        ? raw.GetDecimal()
        : decimal.Parse(raw.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static bool IsBlank(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => string.IsNullOrEmpty(raw.GetString()),
        JsonValueKind.Array => raw.GetArrayLength() == 0,
        _ => false,
    };

    private static string Shorten(JsonElement raw)
    {
        var text = raw.ValueKind == JsonValueKind.Undefined ? "" : raw.GetRawText();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
