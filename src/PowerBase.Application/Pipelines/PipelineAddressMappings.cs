using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// One part of an Address field — Street 1, Street 2, City, State/Region, Postal Code or Country — named by a Create / Update
/// Record mapping as <c>fid_N.city</c> (the same <c>fid_N.part</c> a filter uses, see <see cref="PipelineAddressFieldFilter"/>).
/// The parts a step writes are collected here and merged into the address JSON the field stores, so writing only the City keeps
/// the address's other parts. Every other field's mapping is untouched.
/// </summary>
public sealed class PipelineAddressMappings
{
    private static readonly Regex PartReference = new(@"^fid_\d+\.(street1|street2|city|state|zip|country)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<long, Dictionary<string, string>> _parts = new();

    /// <summary>True for <c>fid_N.city</c>-style references (the field itself is not checked).</summary>
    public static bool IsPartReference(string? fieldRef) => !string.IsNullOrWhiteSpace(fieldRef) && PartReference.IsMatch(fieldRef.Trim());

    public bool HasParts => _parts.Count > 0;

    public IEnumerable<long> Fids => _parts.Keys;

    public void Add(AppField address, string part, string value)
    {
        if (!address.Fid.HasValue) return;
        if (!_parts.TryGetValue(address.Fid.Value, out var parts))
            _parts[address.Fid.Value] = parts = new Dictionary<string, string>(StringComparer.Ordinal);
        parts[part.ToLowerInvariant()] = value;
    }

    /// <summary>Writes each address the parts belong to into <paramref name="values"/>: the parts over the whole address mapped in
    /// the same step, or else over the record's current address (<paramref name="current"/>, for Update Record), or else over an
    /// empty one.</summary>
    public void ApplyTo(IDictionary<long, object?> values, IReadOnlyDictionary<long, object?>? current = null)
    {
        foreach (var (fid, parts) in _parts)
        {
            object? baseValue = values.TryGetValue(fid, out var mapped) ? mapped
                : current != null && current.TryGetValue(fid, out var existing) ? existing
                : null;
            values[fid] = Merge(baseValue, parts);
        }
    }

    /// <summary>The Address parts a Prepare Bulk Record Upsert field list selects (<c>fid_N.city</c>), by Address field; null when
    /// it selects none.</summary>
    public static Dictionary<long, List<string>>? SelectedParts(IEnumerable<string>? selected, IReadOnlyList<AppField> fields)
    {
        Dictionary<long, List<string>>? result = null;
        foreach (var reference in selected ?? [])
        {
            if (!IsPartReference(reference) || !PipelineAddressFieldFilter.TryResolve(reference, fields, out var address, out var part) || !address.Fid.HasValue)
                continue;
            result ??= new Dictionary<long, List<string>>();
            if (!result.TryGetValue(address.Fid.Value, out var parts)) result[address.Fid.Value] = parts = [];
            if (!parts.Contains(part)) parts.Add(part);
        }
        return result;
    }

    /// <summary>Add a Bulk Upsert Row: the parts a row sets on each Address are kept with the row (as a
    /// <see cref="BulkAddressParts"/>) until Commit Upsert knows whether the row updates a record, so an update keeps that record's
    /// other parts and an insert starts from an empty address.</summary>
    public void ApplyToBulkRow(IDictionary<long, object?> row)
    {
        foreach (var (fid, parts) in _parts)
        {
            var whole = row.TryGetValue(fid, out var mapped) && mapped != null && !string.IsNullOrWhiteSpace(mapped.ToString()) ? mapped : null;
            row[fid] = new BulkAddressParts(whole, parts);
        }
    }

    /// <summary>Commit Upsert: turns each row's <see cref="BulkAddressParts"/> into the address JSON, over the matched record's address
    /// (<paramref name="existingRecord"/>, keyed by column) when the row updates one. Rows without address parts are not touched.</summary>
    public static void ResolveBulkRow(IDictionary<long, object?> row, IReadOnlyDictionary<string, object?>? existingRecord, IReadOnlyList<AppField> fields)
    {
        foreach (var fid in row.Keys.ToList())
        {
            if (row[fid] is not BulkAddressParts carried) continue;
            object? baseValue = carried.Whole;
            if (baseValue == null && existingRecord != null && fields.FirstOrDefault(f => f.Fid == fid) is { } field)
                existingRecord.TryGetValue(PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field), out baseValue);
            // Nothing to write over and every part blank: the address stays empty instead of becoming six blank parts.
            row[fid] = (baseValue == null || string.IsNullOrWhiteSpace(baseValue.ToString())) && carried.Parts.Values.All(string.IsNullOrWhiteSpace)
                ? null
                : Merge(baseValue, carried.Parts);
        }
    }

    /// <summary>The parts one bulk row sets on an Address, and the whole address the same row maps (if any).</summary>
    public sealed record BulkAddressParts(object? Whole, IReadOnlyDictionary<string, string> Parts)
    {
        public override string ToString() => Merge(Whole, Parts);
    }

    /// <summary>The address JSON with <paramref name="parts"/> set. Keys the address already has (its other parts, a geocoded
    /// position) are kept; the six parts are always present, as the record form saves them.</summary>
    public static string Merge(object? address, IReadOnlyDictionary<string, string> parts)
    {
        var json = address switch
        {
            null => null,
            JsonElement element => element.ValueKind == JsonValueKind.Object ? element.GetRawText() : element.ValueKind == JsonValueKind.String ? element.GetString() : null,
            _ => address.ToString()
        };
        JsonObject result;
        try { result = !string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith('{') ? JsonNode.Parse(json) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (JsonException) { result = new JsonObject(); }
        foreach (var key in PipelineAddressFieldFilter.Parts)
            if (!result.ContainsKey(key)) result[key] = string.Empty;
        foreach (var (part, value) in parts)
            result[part] = value;
        return result.ToJsonString();
    }
}
