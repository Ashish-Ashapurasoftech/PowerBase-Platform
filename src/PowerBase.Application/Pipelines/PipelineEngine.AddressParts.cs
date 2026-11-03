using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The parts of an Address field — Street 1, Street 2, City, State/Region, Postal Code, Country — as steps read and write them,
/// the way Quickbase offers "Address » City": a token <c>{{steps.ref.fid_N.city}}</c> reads one part of the stored address JSON,
/// and a Create / Update Record mapping to <c>fid_N.city</c> writes one part (see <see cref="PipelineAddressMappings"/>).
/// Only <c>fid_N.part</c> references are handled here; every other token and mapping goes the way it always has.
/// </summary>
public partial class PipelineEngine
{
    private static readonly Regex AddressSubTokenRegex = new(
        @"\{\{\s*([^{}]*?\.fid_\d+)\.(street1|street2|city|state|zip|country)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Swaps each <c>{{…fid_N.city}}</c> for that part of the address the field holds: empty when the address is blank or
    /// has no such part. A token whose field cannot be read is left as written, so its own evaluation reports it.</summary>
    private string ExpandAddressSubTokens(string input, string payloadJson, string? executionPath, List<PipelineStep>? allSteps)
        => AddressSubTokenRegex.Replace(input, match =>
        {
            string raw;
            try { raw = EvaluateTokens("{{" + match.Groups[1].Value + "}}", payloadJson, executionPath, allSteps); }
            catch (Exception) { return match.Value; }
            return PipelineAddressFieldFilter.ReadPart(raw, match.Groups[2].Value.ToLowerInvariant());
        });

    private static bool IsAddressSubToken(string token) =>
        AddressSubTokenRegex.Match(token) is { Success: true } match && match.Length == token.Length;

    /// <summary>A Create / Update Record mapping that writes one part of an Address field (<c>fid_N.city</c>): its value is collected
    /// into <paramref name="parts"/> and written by <see cref="PipelineAddressMappings.ApplyTo"/>. False for every other mapping.
    /// Blank values are skipped, as a blank mapping of any field is.</summary>
    private async Task<bool> TryCollectAddressPartAsync(
        FieldMapping mapping, IReadOnlyList<AppField> fields, PipelineAddressMappings parts, Dictionary<string, object?> resolvedMappings,
        string payloadJson, string? executionPath, List<PipelineStep>? allSteps, IAppFieldRepository fieldRepo, IAppTableRepository tableRepo, CancellationToken ct)
    {
        if (!PipelineAddressMappings.IsPartReference(mapping.Field)
            || !PipelineAddressFieldFilter.TryResolve(mapping.Field, fields, out var address, out var part))
            return false;
        var value = EvaluateTokens(await RenderUserTokensForTextAsync(mapping.Value, address, payloadJson, executionPath, allSteps, fieldRepo, tableRepo, ct),
            payloadJson, executionPath, allSteps);
        if (string.IsNullOrWhiteSpace(value)) return true;
        parts.Add(address, part, value.Trim());
        resolvedMappings[mapping.Field!] = value.Trim();
        return true;
    }

    /// <summary>An Add a Bulk Upsert Row value keyed by one part of an Address (<c>fid_N.city</c>). False for every other key.</summary>
    private bool TryCollectBulkAddressPart(
        string key, object? raw, IReadOnlyList<AppField> fields, PipelineAddressMappings parts, Dictionary<string, object?> resolvedMappings,
        string payloadJson, string? executionPath, List<PipelineStep>? allSteps)
    {
        if (!PipelineAddressMappings.IsPartReference(key) || !PipelineAddressFieldFilter.TryResolve(key, fields, out var address, out var part))
            return false;
        var text = raw switch
        {
            null => null,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Null } => null,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            System.Text.Json.JsonElement element => element.GetRawText(),
            _ => raw.ToString()
        };
        // A bulk row writes every selected column, blank included: a blank part is written blank (the other parts are kept).
        var value = (EvaluateTokens(text, payloadJson, executionPath, allSteps) ?? string.Empty).Trim();
        parts.Add(address, part, value);
        resolvedMappings[key] = value;
        return true;
    }

    /// <summary>The record's current value of each address in <paramref name="fids"/>, so an update of one part keeps the others.</summary>
    private static async Task<IReadOnlyDictionary<long, object?>> CurrentAddressValuesAsync(
        IRecordRepository recordRepo, AppTable table, IReadOnlyList<AppField> fields, Guid recordPublicId, IEnumerable<long> fids, CancellationToken ct)
    {
        var wanted = fids.ToHashSet();
        var result = new Dictionary<long, object?>();
        if (wanted.Count == 0) return result;
        var record = await recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct: ct);
        foreach (var field in fields.Where(f => f.Fid.HasValue && wanted.Contains(f.Fid.Value)))
            if (record.TryGetValue(PhysicalNaming.GetPhysicalColumnName(field), out var value))
                result[field.Fid!.Value] = value;
        return result;
    }
}
