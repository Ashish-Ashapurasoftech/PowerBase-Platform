using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Reports;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.AddLookupFields;
using PowerBase.Application.Relationships.Commands.AddSummaryField;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Import.Services;

/// <summary>A lookup whose source field did not exist yet when its relationship was created.</summary>
public sealed record DeferredLookup(Guid RelationshipPublicId, AppTable ParentTable, PblLookupField Lookup);

/// <summary>A summary whose target field did not exist yet when its relationship was created.</summary>
public sealed record DeferredSummary(Guid RelationshipPublicId, AppTable ChildTable, PblSummaryField Summary);

/// <summary>
/// Lookups, summaries and formula fields can depend on one another in a cycle across tables (a
/// lookup of a formula, a formula over a lookup, a summary of that formula, ...), so no single
/// creation order satisfies them all. The importer creates what it can first, then calls this
/// repeatedly: each round adds whichever deferred lookups/summaries now have their source/target,
/// which in turn lets more formulas compile, until a round makes no progress.
/// </summary>
public class PblDeferredResolver
{
    private readonly AddLookupFieldsCommandHandler _addLookupHandler;
    private readonly AddSummaryFieldCommandHandler _addSummaryHandler;
    private readonly IAppFieldRepository _fieldRepo;

    public PblDeferredResolver(
        AddLookupFieldsCommandHandler addLookupHandler,
        AddSummaryFieldCommandHandler addSummaryHandler,
        IAppFieldRepository fieldRepo)
    {
        _addLookupHandler = addLookupHandler;
        _addSummaryHandler = addSummaryHandler;
        _fieldRepo = fieldRepo;
    }

    /// <summary>Adds every deferred lookup whose source now exists. Returns how many were added;
    /// ones that can never be added are moved to <paramref name="skipped"/> and dropped.</summary>
    public async Task<int> ResolveLookupsAsync(List<DeferredLookup> pending, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var added = 0;
        foreach (var item in pending.ToList())
        {
            var parentFields = await _fieldRepo.ListByTableAsync(item.ParentTable.Id, ct);
            var map = FieldFids.ByNameOrLabel(parentFields);
            if (!map.TryGetValue(item.Lookup.SourceFieldName, out var sourceFid))
                continue; // not there yet — try again next round

            pending.Remove(item);

            if (!string.IsNullOrEmpty(item.Lookup.SourceSubField))
            {
                skipped.Add(Skip(item.Lookup.LogicalRef, item.Lookup.Name, $"Lookup of '{item.Lookup.SourceFieldName}' targets an address part and could not be added after the relationship was created."));
                continue;
            }

            var source = parentFields.First(f => f.Fid == sourceFid);
            var (_, chainLength) = await LookupChain.ResolveAsync(source, _fieldRepo, ct);
            if (chainLength + 1 > LookupChain.MaxLength)
            {
                skipped.Add(Skip(item.Lookup.LogicalRef, item.Lookup.Name, $"Lookup of '{item.Lookup.SourceFieldName}' would chain through more than {LookupChain.MaxLength} lookups, which PowerBase does not allow."));
                continue;
            }

            try
            {
                await _addLookupHandler.HandleAsync(new AddLookupFieldsCommand(
                    item.RelationshipPublicId, [new AddLookupSpec((int)sourceFid, item.Lookup.Label ?? item.Lookup.Name)]), ct);
                added++;
            }
            catch (DomainException ex)
            {
                skipped.Add(Skip(item.Lookup.LogicalRef, item.Lookup.Name, $"Lookup not created: {Describe(ex)}"));
            }
        }
        return added;
    }

    /// <summary>Same as <see cref="ResolveLookupsAsync"/>, for summaries and their child-side target field.</summary>
    public async Task<int> ResolveSummariesAsync(List<DeferredSummary> pending, List<ImportSkippedItem> skipped, CancellationToken ct)
    {
        var added = 0;
        foreach (var item in pending.ToList())
        {
            var childFields = await _fieldRepo.ListByTableAsync(item.ChildTable.Id, ct);
            var map = FieldFids.ByNameOrLabel(childFields);
            var lookupSources = await SummaryLookupSources.LoadAsync(childFields, _fieldRepo, ct);

            // Count / Exists have no target field. Anything else waits until its target exists.
            long? targetFid = null;
            if (item.Summary.TargetFieldName is not null)
            {
                if (!map.TryGetValue(item.Summary.TargetFieldName, out var resolvedTarget))
                    continue;
                targetFid = resolvedTarget;
            }

            // A filter on a field that does not exist yet waits too.
            FilterGroup? matching = null;
            if (item.Summary.Filter is not null)
            {
                matching = PblPermissionsApplier.ToFilterGroup(item.Summary.Filter, childFields, out var filterProblem);
                if (matching is null)
                {
                    if (filterProblem is not null && filterProblem.Contains("does not exist", StringComparison.Ordinal))
                        continue;
                    pending.Remove(item);
                    skipped.Add(Skip(item.Summary.LogicalRef, item.Summary.Name, $"Summary not created: its filter could not be built ({filterProblem})."));
                    continue;
                }
            }

            pending.Remove(item);

            if (!string.IsNullOrEmpty(item.Summary.TargetSubField))
            {
                skipped.Add(Skip(item.Summary.LogicalRef, item.Summary.Name, $"Summary of '{item.Summary.TargetFieldName}' targets an address part and could not be added after the relationship was created."));
                continue;
            }

            var function = SummaryFunctions.Normalize(item.Summary.Function);
            if (function is null)
            {
                skipped.Add(Skip(item.Summary.LogicalRef, item.Summary.Name, $"Unknown summary function '{item.Summary.Function}'."));
                continue;
            }

            try
            {
                var target = targetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == targetFid) : null;
                // A summary of a Reference reads the key its parent shows; only the create handler has the maps to judge that, so it decides.
                if (target?.TypeCode != "Reference")
                    SummaryTargetValidator.Validate(function, (int?)targetFid, target, item.Summary.TargetSubField, lookupSources: lookupSources);
                await _addSummaryHandler.HandleAsync(new AddSummaryFieldCommand(
                    item.RelationshipPublicId, item.Summary.Label ?? item.Summary.Name, function, (int?)targetFid, matching), ct);
                added++;
            }
            catch (DomainException ex)
            {
                skipped.Add(Skip(item.Summary.LogicalRef, item.Summary.Name, $"Summary not created: {Describe(ex)}"));
            }
        }
        return added;
    }

    private static ImportSkippedItem Skip(string logicalRef, string name, string reason) =>
        new() { LogicalRef = logicalRef, Name = name, Reason = reason };

    private static string Describe(DomainException ex) =>
        ex is ValidationException ve ? string.Join(" ", ve.Errors.SelectMany(e => e.Value)) : ex.Message;
}

/// <summary>Field Fid lookup shared by the importer passes.</summary>
public static class FieldFids
{
    /// <summary>Field Fid by Name, with each field's Label as a fallback key. Imported files refer to
    /// fields by their source label ("Record ID#", "Date Modified"), but PowerBase's own system
    /// fields carry generated Names (<c>S_recordId</c>) — the label is the only thing they share.</summary>
    public static Dictionary<string, long> ByNameOrLabel(IEnumerable<AppField> fields)
    {
        var withFid = fields.Where(f => f.Fid.HasValue).ToList();
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in withFid)
            map.TryAdd(f.Name, f.Fid!.Value);
        foreach (var f in withFid.Where(f => !string.IsNullOrWhiteSpace(f.Label)))
            map.TryAdd(f.Label!.Trim(), f.Fid!.Value);
        return map;
    }
}
