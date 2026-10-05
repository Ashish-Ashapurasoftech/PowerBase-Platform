using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// A Lookup may pull down another Lookup (Salary → Payroll → Employee). This resolves what such a
/// chain ends in — the underlying field's type, which is what the new lookup is stored and typed as —
/// and caps how many lookups deep the chain may go.
/// </summary>
public static class LookupChain
{
    /// <summary>Most lookups that may be chained (the new lookup counts as one). Also bounds how
    /// deep RelationalProjector recurses, so a cycle of lookups can't loop forever.</summary>
    public const int MaxLength = 3;

    /// <summary>Walks <paramref name="source"/> down through any lookups. Returns the type of the
    /// field the chain ends in and how many lookups the chain already contains (0 when the source
    /// isn't a lookup). A broken or cyclic chain ends at the last lookup's stored SourceTypeCode.</summary>
    public static async Task<(string TypeCode, int Length)> ResolveAsync(
        AppField source, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var length = 0;
        var current = source;
        var visited = new HashSet<long>();
        while (SummaryLookupSources.IsLookup(current))
        {
            length++;
            var settings = FormulaTypeMap.ParseLookupSettings(current.Settings);
            var stored = string.IsNullOrWhiteSpace(settings?.SourceTypeCode) ? "Text" : settings!.SourceTypeCode!;
            if (length > MaxLength || !visited.Add(current.Id)
                || settings is not { SourceTableId: not null, SourceFid: not null })
                return (Underlying(stored), length);

            var next = (await fieldRepo.ListByTableAsync(settings.SourceTableId.Value, ct))
                .FirstOrDefault(f => f.Fid == settings.SourceFid);
            if (next is null) return (Underlying(stored), length);
            current = next;
        }
        return (current.TypeCode, length);
    }

    /// <summary>Resolves the source's underlying type and refuses a lookup that would make the
    /// chain longer than <see cref="MaxLength"/>.</summary>
    public static async Task<string> ResolveForNewLookupAsync(
        AppField source, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var (typeCode, length) = await ResolveAsync(source, fieldRepo, ct);
        if (length + 1 > MaxLength)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["sourceFid"] = [$"A lookup can be chained through at most {MaxLength} lookups; \"{source.Label ?? source.Name}\" is already {length} deep."],
            });
        return typeCode;
    }

    // A stored type is never "Lookup" for lookups made after this change; older data might be.
    private static string Underlying(string stored) => stored == "Lookup" ? "Text" : stored;
}
