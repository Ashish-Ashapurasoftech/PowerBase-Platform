using System.Collections.Generic;
using System.Linq;

namespace PowerBase.Application.Search;

public static class SearchResultMerger
{
    /// <summary>
    /// Merges the best results of several indexes into one ranking: highest relevance first, then the requested slice. Each index must
    /// supply at least its best (<paramref name="skip"/> + <paramref name="take"/>) results, or the page can miss rows.
    /// </summary>
    public static IReadOnlyList<T> MergeByScore<T>(IEnumerable<IReadOnlyList<(double Score, T Item)>> perIndex, int skip, int take) =>
        perIndex
            .SelectMany(index => index)
            .OrderByDescending(r => r.Score)
            .Skip(skip)
            .Take(take)
            .Select(r => r.Item)
            .ToList();
}
