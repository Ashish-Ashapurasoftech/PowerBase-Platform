using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>Guards used when a Lookup's source is changed (Lookup field options).</summary>
public static class LookupSourceRules
{
    /// <summary>True when following <paramref name="source"/> down its lookup chain reaches the field
    /// <paramref name="lookupId"/> — pulling it would make the lookup read itself (possible with a self-relationship or a
    /// loop of relationships).</summary>
    public static async Task<bool> ChainReachesAsync(AppField source, long lookupId, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var current = source;
        for (var depth = 0; depth <= LookupChain.MaxLength; depth++)
        {
            if (current.Id == lookupId) return true;
            if (!SummaryLookupSources.IsLookup(current)) return false;
            var s = FormulaTypeMap.ParseLookupSettings(current.Settings);
            if (s is not { SourceTableId: long tableId, SourceFid: int fid }) return false;
            var next = (await fieldRepo.ListByTableAsync(tableId, ct)).FirstOrDefault(f => f.Fid == fid);
            if (next is null) return false;
            current = next;
        }
        return false;
    }
}
