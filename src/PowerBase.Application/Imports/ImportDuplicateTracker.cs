namespace PowerBase.Application.Imports;

/// <summary>Remembers, for the whole run rather than one chunk, which values each column has already carried, so a
/// duplicate is caught however far apart its rows are. Values are kept as 64-bit hashes of their normalised text
/// (8 bytes each, however long the text), so tracking millions of values stays small. A collision would need two
/// different values to hash alike: for a million values the odds are below one in ten million.</summary>
public sealed class ImportDuplicateTracker
{
    private readonly Dictionary<int, HashSet<ulong>> _seen = new();
    private readonly Dictionary<int, HashSet<ulong>> _existing = new();
    private readonly Dictionary<int, HashSet<ulong>> _groups;

    /// <param name="duplicateGroups">Values known (from a scan of the whole source) to appear more than once, by column.
    /// Null when no scan was made.</param>
    public ImportDuplicateTracker(Dictionary<int, HashSet<ulong>>? duplicateGroups = null)
    {
        _groups = duplicateGroups ?? new Dictionary<int, HashSet<ulong>>();
    }

    /// <summary>Values met more than once while scanning, by column. Handed to the pass that does the writing.</summary>
    public Dictionary<int, HashSet<ulong>> DuplicateGroups => _groups;
    public bool HasDuplicateGroups => _groups.Values.Any(g => g.Count > 0);

    /// <summary>True when the value was already seen in this run.</summary>
    public bool WasSeen(int fid, string key) => Contains(_seen, fid, key);

    /// <summary>Records the value as seen. Returns false when it already was.</summary>
    public bool MarkSeen(int fid, string key) => Add(_seen, fid, key);

    public void AddExisting(int fid, string key) => Add(_existing, fid, key);
    public bool ExistsInDestination(int fid, string key) => Contains(_existing, fid, key);

    public void MarkDuplicateGroup(int fid, string key) => Add(_groups, fid, key);
    public bool IsInDuplicateGroup(int fid, string key) => Contains(_groups, fid, key);

    private static bool Add(Dictionary<int, HashSet<ulong>> map, int fid, string key)
    {
        if (!map.TryGetValue(fid, out var set)) map[fid] = set = new HashSet<ulong>();
        return set.Add(Hash(key));
    }

    private static bool Contains(Dictionary<int, HashSet<ulong>> map, int fid, string key) =>
        map.TryGetValue(fid, out var set) && set.Contains(Hash(key));

    /// <summary>FNV-1a over the lower-cased characters, so "ABC" and "abc" are the same value, as in the database.</summary>
    private static ulong Hash(string key)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in key)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
