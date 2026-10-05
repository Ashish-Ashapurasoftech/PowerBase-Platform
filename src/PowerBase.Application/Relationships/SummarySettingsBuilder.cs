using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Validates a Summary field's definition (function, target, Combined Text options, matching
/// criteria, encryption) and builds the <see cref="SummarySettings"/> to store. Shared by Add and
/// Update Summary so a summary can never be edited into a shape it couldn't have been created with.
/// </summary>
public static class SummarySettingsBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Throws <see cref="ValidationException"/> when the definition can't be used.</summary>
    /// <param name="lookupSources">The parent field each child lookup pulls down
    /// (<see cref="SummaryLookupSources.LoadAsync"/>) — lookups can be summarized, sorted and
    /// filtered on through it.</param>
    public static SummarySettings Build(
        Relationship rel,
        string? functionName,
        int? targetFid,
        FilterGroup? matchingCriteria,
        CombinedTextOptions? combinedTextOptions,
        IReadOnlyList<AppField> childFields,
        IReadOnlyList<AppField> parentFields,
        App app,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        // Stored in canonical casing ("sum" → "Sum"): the aggregation code matches names exactly.
        var function = SummaryFunctions.Normalize(functionName)
            ?? throw new ValidationException(new Dictionary<string, string[]> { ["function"] = [$"Unknown summary function '{functionName}'."] });

        var needsTarget = function is not (SummaryFunctions.Count or SummaryFunctions.Exists);

        var target = targetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == targetFid) : null;
        SummaryTargetValidator.Validate(function, targetFid, target, targetSubField: null, lookupSources: lookupSources);
        var combinedText = SummaryTargetValidator.ValidateCombinedTextOptions(function, combinedTextOptions, childFields, lookupSources);
        SummaryCriteriaValidator.Validate(matchingCriteria, childFields, parentFields, lookupSources);

        // Encrypted columns can't be summarized yet — refuse rather than create a summary that
        // would compute nothing (or expose ciphertext).
        if (SummaryEncryptionGuard.FindProblem(app, childFields, rel.ReferenceFid,
                needsTarget ? targetFid : null, combinedText?.SortFid, matchingCriteria, parentFields, lookupSources) is { } encryptionProblem)
            throw new ValidationException(new Dictionary<string, string[]> { ["targetFid"] = [encryptionProblem] });

        // A lookup's values are its parent field's type (Min of a looked-up date is a date).
        var targetTypeCode = target is not null && SummaryLookupSources.IsLookup(target)
            ? SummaryLookupSources.ReadableSource(target, lookupSources)?.TypeCode
            : target?.TypeCode;

        return new SummarySettings
        {
            RelationshipId = rel.Id,
            ChildTableId = rel.ChildTableId,
            ReferenceFid = rel.ReferenceFid,
            Function = function,
            TargetFid = needsTarget ? targetFid : null,
            TargetTypeCode = targetTypeCode,
            FilterTree = matchingCriteria is { Nodes.Count: > 0 }
                ? JsonSerializer.Serialize(matchingCriteria, JsonOpts)
                : null,
            Delimiter = combinedText?.Delimiter,
            SortFid = combinedText?.SortFid,
            SortDescending = combinedText?.SortDescending ?? false,
            DistinctValues = combinedText?.DistinctValues ?? false,
        };
    }
}
