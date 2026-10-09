using System.Runtime.CompilerServices;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Reads every document id that matches an Azure AI Search filter, however many there are.
///
/// A query returns at most 1,000 documents, and paging with skip is neither stable while the index changes nor
/// unlimited. The key field (<c>id</c>, a lower-case GUID string) is filterable but not known to be sortable, so this
/// pages by <em>range</em> instead: it asks how many documents match inside an id range (<c>id ge 'a3' and id lt 'a7'</c>),
/// and when that is more than one page it splits the range and recurses, in id order. A range that fits one page is read
/// in that same query. No skip, no sort and no index change are needed.
///
/// A range is split into just enough pieces for each to hold about <see cref="TargetPageSize"/> documents (the count it
/// just got says how many that is), by grouping adjacent next-characters — so a clump of ids is split finely and a thin
/// stretch not at all. Splitting always into one piece per character (16 ways) would leave most pages nearly empty: at 6
/// million matches that is ~70,000 queries, against ~12,000 this way.
///
/// The cursor is the (exclusive) upper bound of the last range read: ranges at or below it are skipped on resume
/// without being queried, so an interrupted read continues where it stopped.
/// </summary>
public static class AiIdRangePager
{
    /// <summary>Cursor of a read that has reached the end of the id space.</summary>
    public const string EndCursor = AiSearchIdPage.EndCursor;

    /// <summary>The most documents one query returns.</summary>
    public const int PageSize = 1000;

    /// <summary>How many documents a split piece is aimed at: under <see cref="PageSize"/> so that ordinary variation does not
    /// push a piece over it (which would cost another split), but close to it so pages stay full.</summary>
    public const int TargetPageSize = 800;

    private const int GuidLength = 36;
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Runs one query: the ids it returned (at most <paramref name="size"/>) and how many documents match in all.
    /// A total of -1 means the service gave no count.</summary>
    public delegate Task<(IReadOnlyList<Guid> Ids, long Total)> QueryAsync(string filter, int size, CancellationToken ct);

    /// <summary>A range of ids: the characters From..To (inclusive) that can follow <see cref="Prefix"/>. From = 0 reaches down to the
    /// prefix itself and To = the last character up to the end of the prefix's range, so adjacent nodes tile with no gap.</summary>
    private readonly record struct Node(string Prefix, int From, int To)
    {
        public int Width => To - From + 1;
    }

    public static async IAsyncEnumerable<AiSearchIdPage> PageAsync(
        string baseFilter, string? afterCursor, QueryAsync query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = string.IsNullOrEmpty(afterCursor) ? "" : afterCursor;
        if (cursor == EndCursor) yield break;

        // Depth-first in id order.
        var pending = new Stack<Node>();
        pending.Push(Whole(""));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = pending.Pop();
            var (lower, upper) = Bounds(node);

            // Already read (everything in this range is below the resume point).
            if (upper != null && cursor.Length > 0 && string.CompareOrdinal(upper, cursor) <= 0) continue;

            // The resume point lies inside this range: it is a boundary of a finer split, so go down one piece per character
            // without querying.
            if (string.CompareOrdinal(lower, cursor) < 0)
            {
                if (!Split(pending, node, total: -1)) throw CannotSplit(node, -1);
                continue;
            }

            var (ids, total) = await query(Compose(baseFilter, lower, upper), PageSize, ct);
            if (total >= 0 && total <= PageSize && ids.Count >= total)
            {
                if (ids.Count > 0) yield return new AiSearchIdPage(ids, upper ?? EndCursor);
                continue;
            }

            // More than one page here (or the count and the ids disagree): split.
            if (!Split(pending, node, total)) throw CannotSplit(node, total);
        }
    }

    /// <summary>The filter for one range inside <paramref name="baseFilter"/>.</summary>
    public static string Compose(string baseFilter, string lower, string? upper)
    {
        var filter = $"({baseFilter})";
        if (lower.Length > 0) filter += $" and id ge '{lower}'";
        if (upper != null) filter += $" and id lt '{upper}'";
        return filter;
    }

    /// <summary>The smallest string above every id that starts with <paramref name="prefix"/>; null when there is none.</summary>
    public static string? Successor(string prefix)
    {
        var chars = prefix.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var c = chars[i];
            if (c == 'f') continue;   // carries into the previous character
            chars[i] = c switch { '9' => 'a', '-' => '.', _ => (char)(c + 1) };
            return new string(chars, 0, i + 1);
        }
        return null;
    }

    /// <summary>The next characters an id can have after <paramref name="prefix"/>: a GUID string is hex digits with a hyphen
    /// after the 8th, 12th, 16th and 20th.</summary>
    public static IReadOnlyList<char> NextCharacters(string prefix)
    {
        if (prefix.Length >= GuidLength) return [];
        return prefix.Length is 8 or 13 or 18 or 23 ? ['-'] : HexDigits.ToCharArray();
    }

    private static Node Whole(string prefix) => new(prefix, 0, NextCharacters(prefix).Count - 1);

    /// <summary>The [lower, upper) range a node covers; upper null means no upper bound.</summary>
    private static (string Lower, string? Upper) Bounds(Node node)
    {
        var next = NextCharacters(node.Prefix);
        var lower = node.From == 0 ? node.Prefix : node.Prefix + next[node.From];
        var upper = node.To == next.Count - 1 ? Successor(node.Prefix) : node.Prefix + next[node.To + 1];
        return (lower, upper);
    }

    /// <summary>Replaces a node by smaller ones, pushed so the lowest is read first. <paramref name="total"/> is the number of
    /// documents it holds (-1 when unknown: split as finely as possible). False when it cannot be split any further.</summary>
    private static bool Split(Stack<Node> pending, Node node, long total)
    {
        // A node that is a single character is the same range as the prefix it adds: split that prefix's characters instead.
        while (node.Width == 1)
        {
            var child = node.Prefix + NextCharacters(node.Prefix)[node.From];
            node = Whole(child);
            if (node.Width <= 0) return false;
        }
        if (node.Width <= 0) return false;

        var pieces = total > PageSize
            ? (int)Math.Min(node.Width, Math.Max(2, (total + TargetPageSize - 1) / TargetPageSize))
            : node.Width;

        var size = node.Width / pieces;
        var extra = node.Width % pieces;
        var starts = new List<(int From, int To)>(pieces);
        var from = node.From;
        for (var i = 0; i < pieces; i++)
        {
            var width = size + (i < extra ? 1 : 0);
            starts.Add((from, from + width - 1));
            from += width;
        }
        for (var i = starts.Count - 1; i >= 0; i--) pending.Push(new Node(node.Prefix, starts[i].From, starts[i].To));
        return true;
    }

    private static InvalidOperationException CannotSplit(Node node, long total)
    {
        var (lower, _) = Bounds(node);
        return new InvalidOperationException(
            $"Azure AI Search reported {total} documents for the id range starting '{lower}', which cannot be split further.");
    }
}
