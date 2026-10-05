using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>How a Combined Text summary joins its values — see the matching properties on
/// <see cref="SummarySettings"/>. <paramref name="SortFid"/> null ⇒ record creation order.</summary>
public record CombinedTextOptions(string Delimiter, int? SortFid, bool SortDescending, bool DistinctValues)
{
    public static readonly CombinedTextOptions Default = new(SummaryFunctions.DefaultCombinedTextDelimiter, null, false, false);

    public static CombinedTextOptions From(SummarySettings s) =>
        new(s.Delimiter ?? SummaryFunctions.DefaultCombinedTextDelimiter, s.SortFid, s.SortDescending, s.DistinctValues);
}
